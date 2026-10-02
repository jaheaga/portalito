using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Collage;
using Jellyfin.Plugin.Portalito.Live;
using Jellyfin.Plugin.Portalito.Metadata;
using Jellyfin.Plugin.Portalito.Portal;
using Jellyfin.Plugin.Portalito.Proxy;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>
/// The portal as a Jellyfin channel. The root holds two folders: "Descubrir" (Portalito's own full, paginated catalog) and
/// "TV en vivo" (the live channels). Playback goes through the plugin's own signed proxy URLs.
/// </summary>
public sealed class PortalitoVodChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    /// <summary>Portal page size; also the channel's <c>MaxPageSize</c> so a Jellyfin window spans at most two portal pages.</summary>
    internal const int PageSize = 30;

    // program types the reference client treats as "a series of episodes"; anything else plays directly as a movie.
    private static readonly HashSet<string> SeriesTypes = new(StringComparer.OrdinalIgnoreCase) { "teleplay", "series", "variety" };

    // A "Destacado" row built from a TMDB list: VodItemId.Row(DiscoveryBrowser.RowKey(label), TmdbRowMode).
    private const string TmdbRowMode = "tmdb";

    /// <summary>How long a title's probe may take before playback goes ahead without its track list.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long one listing may spend filling blanks from TMDB.</summary>
    internal static readonly TimeSpan TmdbBudget = TimeSpan.FromSeconds(5);

    internal const int TmdbConcurrency = 8;

    private readonly IPortalitoServicesProvider _services;
    private readonly IVodTrackProbe _probe;
    private readonly SubtitleFileCache _subtitles;
    private readonly CollageService _collages;
    private readonly ILogger<PortalitoVodChannel> _logger;
    private readonly RuntimeStore _runtimes;
    private readonly ILibraryManager? _library;

    // A title's tracks never change, so each is probed once; only the first playback of a title pays for it.
    private readonly TtlCache<ProbedTracks> _probed = new(TimeProvider.System, TimeSpan.FromDays(7), capacity: 2000);

    // A live category's first channel logos, for its collage thumbnail.
    private readonly TtlCache<IReadOnlyList<CollageImage>> _liveLogos = new(TimeProvider.System, TimeSpan.FromHours(24), capacity: 200);

    // Portal calls for collage candidates, across every listing at once: opening the root plans all four catalogs'
    // genres plus every live category, and the portal has returned incomplete listings under bursts (2026-09-21).
    private readonly SemaphoreSlim _thumbnailFetches = new(6);

    // Collage renders (four poster downloads each), across every listing at once.
    private readonly SemaphoreSlim _collageRenders = new(4);

    /// <param name="runtimes">Known title runtimes (see <see cref="RuntimeStore"/>); in-memory only when not given.</param>
    /// <param name="library">Jellyfin's library, to give a playing item its runtime right away; optional (tests).</param>
    public PortalitoVodChannel(
        IPortalitoServicesProvider services,
        IVodTrackProbe probe,
        SubtitleFileCache subtitles,
        CollageService collages,
        ILogger<PortalitoVodChannel> logger,
        RuntimeStore? runtimes = null,
        ILibraryManager? library = null)
    {
        _runtimes = runtimes ?? new RuntimeStore(null);
        _library = library;
        _services = services;
        _probe = probe;
        _subtitles = subtitles;
        _collages = collages;
        _logger = logger;
    }

    public string Name => "Portalito VOD";

    /// <summary>
    /// Part of the file name Jellyfin caches each folder listing under (for 3 hours, alongside <see cref="DataVersion"/>).
    /// It changes whenever the configuration does, so saving a setting -- the Destacado rows, the catalogs, the TMDB key --
    /// shows up in the very next listing. Measured 2026-09-30: without it, changed rows stayed hidden behind the cached
    /// listing. See <see cref="PortalitoRuntime.CacheStamp"/> for why it isn't a hash of the configuration.
    /// </summary>
    public string? GetCacheKey(string? userId) => _services.CacheStamp();

    public string Description => "Movies and series from a configurable IPTV portal.";

    /// <summary>
    /// Jellyfin persists channel item listings keyed by (channel, folder, DataVersion), with its own further
    /// server-side cache on top (Jellyfin.LiveTv/Channels/ChannelManager.cs). Bucketing by UTC day forces at
    /// least a daily re-fetch without a manual bump each time; bump the prefix for any change to the id scheme,
    /// the mapping, or the shelf/catalog-fetch logic -- including a per-item field, not just the folder tree
    /// shape (confirmed live repeatedly 2026-09-21/23: neither redeploying code nor restarting Jellyfin changes
    /// what a folder already cached under today's key serves; only the key itself changing does).
    /// "v10" -> "v11" (2026-09-23): replaced the TMDB/AniList "Descubrir" layer (picked titles that often weren't
    /// even in Portalito) with Portalito's own full catalog via <c>v3/filterByContent</c> (see <see cref="DiscoverCatalogFoldersAsync"/>),
    /// and added real metadata (overview, year, rating, genres, cast) to every item from fields the portal already
    /// sends but were previously ignored.
    /// "v11" -> "v11b" (2026-09-23): a show's season folders were reusing the native <c>ser:</c> id for the same
    /// content id, so Jellyfin served the native shelf's already-persisted raw title/no-IndexNumber for that GUID
    /// instead of this listing's "Temporada N"/IndexNumber (see <see cref="VodItemKind.ShowSeason"/>); "v11a" was a
    /// throwaway diagnostic-only bump used to confirm the raw <c>sameSeasonSeriesList</c> shape before landing the fix.
    /// "v11b" -> "v12" (2026-09-23): posters now go through the plugin's own image proxy (<see cref="ProxyUrlSigner.ImageUrl"/>)
    /// instead of hotlinking Portalito's CDN directly (its non-standard <c>image/jpg</c> Content-Type broke every poster
    /// in Jellyfin's own image cache/converter), and catalog/genre/year folders now carry a representative thumbnail
    /// where a native shelf or filtered listing previously left them blank.
    /// "v12" -> "v13" (2026-09-23): a show's season folders (<see cref="ShowSeasonsAsync"/>) were tagged
    /// <c>ChannelFolderType.Series</c> instead of <c>.Season</c> -- a Series folder nested directly inside the show's
    /// own Series folder confused Jellyfin's own SeriesMetadataService, which synthesized a bogus "Season Unknown"
    /// (confirmed live: a real episode's <c>SeasonId</c> came back null) and, at least once, crashed
    /// <c>ChannelManager.GetChannelItemsInternal</c> with a NullReferenceException. Reported by the owner as
    /// "series don't play" -- episodes listed fine but playback failed. Bumping alone does not repair a BaseItem
    /// Jellyfin already corrupted under the old FolderType; those still need cleaning up (see PROGRESS.md).
    /// "v13" -> "v14" (2026-09-23): the root no longer lists the native portal shelves (10 items each, a hard portal
    /// cap); it is just "Descubrir" plus a new "TV en vivo" folder of live channels.
    /// "v14" -> "v15" (2026-09-23): "TV en vivo" lists the portal's own live categories (by country, by theme)
    /// instead of one flat list of 800+ channels.
    /// "v15" -> "v16" (2026-09-24): "Por año" folders list the whole year (paged), not only its newest 200.
    /// "v16" -> "v17" (2026-09-24): Descubrir listings fill blank synopses/missing posters from TMDB when a key is set.
    /// "v17" -> "v18" (2026-09-24): every folder (root, catalogs, genres, years, live categories) gets a collage
    /// thumbnail of its own titles/channel logos with its name (see <see cref="CollageRenderer"/>).
    /// "v18" -> "v19" (2026-09-24): collages picked most-specific-first so siblings and parents stop repeating the
    /// same newest titles (see <see cref="CollagePlanner"/>).
    /// "v19" -> "v20" (2026-09-24): folder images are rendered collage files, not URLs -- Jellyfin's folder image
    /// provider was replacing downloaded collages with a single child's poster (see <see cref="CollageService"/>).
    /// "v20" -> "v21" (2026-09-24): collages pick by show, not poster URL, so a series' seasons ("The Simpsons" in
    /// every 1990s year) count once (see <see cref="CollageImage"/>).
    /// "v21" -> "v22" (2026-09-28): a new "Destacado" root of curated rows (Estrenos, Mejor valoradas) leads the tree.
    /// "v22" -> "v23" (2026-09-30): "Destacado" rows can come from TMDB lists (the FeaturedRows setting), reconciled
    /// with the portal (see <see cref="DiscoveryBrowser"/>).
    /// "v23" -> "v24" (2026-10-01): movies and episodes carry their runtime once known, and episodes their season and
    /// show name -- what Jellyfin needs to keep playback positions (Continue Watching) and order episodes.
    /// "v24" -> "v25" (2026-10-01): TMDB Destacado rows are identified by their label, not their position (see
    /// <see cref="DiscoveryBrowser.RowKey"/>).
    /// </summary>
    public string DataVersion => $"v25:{DateTime.UtcNow:yyyyMMdd}";

    public string HomePageUrl => string.Empty;

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>Hides the channel until the plugin is configured, rather than offering a channel that only errors.</summary>
    public bool IsEnabledFor(string userId)
    {
        try
        {
            _services.Get();
            return true;
        }
        catch (PortalException)
        {
            return false;
        }
    }

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie, ChannelMediaContentType.Episode },
        MaxPageSize = PageSize,
    };

    // This is the CHANNEL's own icon (Jellyfin's channel-list entry for "Portalito VOD" itself), not a per-item
    // poster -- those are set directly on each ChannelItemInfo.ImageUrl. No source has a logo for the channel as
    // a whole, so this stays empty.
    /// <summary>
    /// The channel's own images: the configured <c>ChannelImageUrl</c> when set, otherwise the built-in Portalito logo
    /// (square, for Primary) and banner (16:9, for Thumb and Backdrop), embedded from <c>assets/branding</c>. Works even
    /// before the portal is configured, so the channel never shows a blank tile.
    /// </summary>
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        string? url = null;
        try
        {
            url = _services.Get().ChannelImageUrl;
        }
        catch (PortalException)
        {
            // Not configured yet: the built-in image still applies.
        }

        if (url is not null)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = true, Path = url, Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http });
        }

        var resource = type == ImageType.Primary ? "icon.png" : "banner.png";
        var stream = typeof(PortalitoVodChannel).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Portalito.Branding." + resource);
        return Task.FromResult(stream is null
            ? new DynamicImageResponse { HasImage = false }
            : new DynamicImageResponse { HasImage = true, Stream = stream, Format = MediaBrowser.Model.Drawing.ImageFormat.Png });
    }

    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop };

    /// <summary>
    /// Jellyfin 10.11's ChannelManager calls this ONCE per folder with no StartIndex/Limit at all, caches the whole
    /// result to disk, and pages that cached list itself -- TotalRecordCount is never used to ask for more. So a
    /// missing Limit means "everything": defaulting it to <see cref="PageSize"/> silently caps a folder at 30 items.
    /// </summary>
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var services = _services.Get();
        var window = PageWindow.Create(query.StartIndex, query.Limit ?? int.MaxValue, PageSize);

        if (string.IsNullOrEmpty(query.FolderId))
        {
            return Slice(await RootFoldersAsync(services, cancellationToken).ConfigureAwait(false), window);
        }

        if (!VodItemId.TryParse(query.FolderId, out var folder))
        {
            throw new ArgumentException($"Unrecognised folder id '{query.FolderId}'.", nameof(query));
        }

        switch (folder.Kind)
        {
            case VodItemKind.LiveRoot:
                return Slice(await LiveCategoryFoldersAsync(services, cancellationToken).ConfigureAwait(false), window);
            case VodItemKind.LiveCategory:
                return Slice(await LiveChannelsAsync(services, long.Parse(folder.Primary, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false), window);
            case VodItemKind.ShowSeason:
                var season = await services.Portal.SeasonAsync(folder.Primary, cancellationToken).ConfigureAwait(false);
                return Slice(season.Episodes.Select(e => EpisodeItem(services.Signer, folder.Primary, e, season, _runtimes)).OfType<ChannelItemInfo>().ToList(), window);
            case VodItemKind.Featured:
                return Slice(await DestacadoFoldersAsync(services, cancellationToken).ConfigureAwait(false), window);
            case VodItemKind.Row when folder.Secondary == TmdbRowMode:
                return await TmdbRowItemsAsync(services, ParseCatalogIndex(folder.Primary), window, cancellationToken).ConfigureAwait(false);
            case VodItemKind.Row or VodItemKind.Catalog or VodItemKind.Filter when !HasCatalog(services, folder.Primary):
                // A folder saved before the catalog list shrank: nothing to list, rather than an error.
                return Slice(Array.Empty<ChannelItemInfo>(), window);
            case VodItemKind.Row:
                return await RowItemsAsync(services, ParseCatalogIndex(folder.Primary), folder.Secondary!, window, cancellationToken).ConfigureAwait(false);
            case VodItemKind.Discover:
                return Slice(await DiscoverCatalogFoldersAsync(services, cancellationToken).ConfigureAwait(false), window);
            case VodItemKind.Catalog:
                var catalogIndex = ParseCatalogIndex(folder.Primary);
                return Slice(await CatalogFoldersAsync(services, catalogIndex, cancellationToken).ConfigureAwait(false), window);
            case VodItemKind.Filter:
                return await FilterItemsAsync(services, ParseCatalogIndex(folder.Primary), folder.Secondary!, window, cancellationToken).ConfigureAwait(false);
            case VodItemKind.Show:
                return await ShowSeasonsAsync(services, folder.Primary, window, cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentException($"'{query.FolderId}' is not a folder.", nameof(query));
        }
    }

    /// <summary>
    /// Resolves through the portal (unlike browsing, which never calls it) because the real container -- "ts" or
    /// "mp4", from the portal's own videoFormat -- has to be known before ffmpeg opens the proxy URL: requesting the
    /// wrong CDN extension serves a different, invalid file (measured live 2026-09-21). The proxy re-resolves the
    /// same content moments later when ffmpeg's request actually arrives (its own cache, not shared with this call);
    /// one extra portal round trip per playback start is an acceptable cost for a correct container every time.
    /// </summary>
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        if (!VodItemId.TryParse(id, out var item))
        {
            throw new ArgumentException($"Unrecognised item id '{id}'.", nameof(id));
        }

        var services = _services.Get();
        if (item.Kind == VodItemKind.LiveChannel)
        {
            // Same signed HLS proxy source Jellyfin's Live TV section plays; no portal call until ffmpeg fetches it.
            var live = MediaSources.Live(item.Primary, services.Signer.LivePlaylistUrl(item.Primary, MediaSources.LiveUrlValidity));
            live.Id = MediaSources.SourceIdFor(id);
            return new[] { live };
        }

        var (contentId, seriesId) = item.Kind switch
        {
            VodItemKind.Movie => (item.Primary, string.Empty),
            VodItemKind.Episode => (item.Secondary!, item.Primary),
            _ => throw new ArgumentException($"'{id}' is not playable.", nameof(id)),
        };

        var stream = await services.Portal.ResolveVodAsync(contentId, seriesId, cancellationToken).ConfigureAwait(false);
        var url = item.Kind == VodItemKind.Movie
            ? services.Signer.VodUrl(item.Primary, string.Empty, MediaSources.UrlValidity)
            : services.Signer.VodUrl(item.Secondary!, item.Primary, MediaSources.UrlValidity);
        var container = stream.VideoFormat == "ts" ? "ts" : "mp4";

        var source = MediaSources.Vod(id, url, container);
        var probing = ProbeCachedAsync(contentId, source, cancellationToken);
        var subtitles = await _subtitles.LocalCopiesAsync(stream.Subtitles ?? Array.Empty<SubtitleFile>(), cancellationToken).ConfigureAwait(false);
        var tracks = await probing.ConfigureAwait(false);
        if (tracks is not null)
        {
            source.RunTimeTicks = tracks.RunTimeTicks;
            source.Bitrate = tracks.Bitrate;
            source.MediaStreams = VodTracks.Assemble(tracks.Streams, subtitles);
        }
        else
        {
            // Unprobed, the source plays as before -- ffmpeg picks the default track -- and the subtitles are still offered.
            source.MediaStreams = VodTracks.Assemble(Array.Empty<MediaStream>(), subtitles);
        }

        // Remember the runtime, and fall back to a remembered one when this probe failed.
        if (tracks?.RunTimeTicks is > 0 and var probed)
        {
            _runtimes.Set(contentId, probed, DateTime.UtcNow);
        }
        else if (_runtimes.TryGet(contentId, out var known))
        {
            source.RunTimeTicks = known.Ticks;
        }

        if (source.RunTimeTicks is > 0 and var runtime)
        {
            await GiveItemRuntimeAsync(id, item.Kind, runtime, cancellationToken).ConfigureAwait(false);
        }

        return new[] { source };
    }

    /// <summary>
    /// Sets the playing item's runtime in Jellyfin's library now -- this callback runs before playback starts, so the
    /// first progress report already finds it and the position is kept. Jellyfin names a channel item
    /// <c>GetNewItemId(externalId + channelName + "16", type)</c> (v10.11 <c>ChannelManager.GetIdToHash</c>).
    /// A failure here only means this one playback isn't resumable; it never stops playback.
    /// </summary>
    private async Task GiveItemRuntimeAsync(string channelItemId, VodItemKind kind, long ticks, CancellationToken cancellationToken)
    {
        if (_library is null)
        {
            return;
        }

        try
        {
            var type = kind == VodItemKind.Episode ? typeof(MediaBrowser.Controller.Entities.TV.Episode) : typeof(MediaBrowser.Controller.Entities.Movies.Movie);
            var libraryId = _library.GetNewItemId(channelItemId + Name + "16", type);
            if (_library.GetItemById(libraryId) is { } entity && entity.RunTimeTicks != ticks)
            {
                entity.RunTimeTicks = ticks;
                await entity.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't give {Item} its runtime; this playback won't be resumable", channelItemId);
        }
    }

    /// <summary>
    /// The title's probed tracks, or null if probing failed or took longer than <see cref="ProbeTimeout"/> -- a slow or
    /// broken probe must not stop playback. Failures are not cached, so the next playback tries again.
    /// </summary>
    private async Task<ProbedTracks?> ProbeCachedAsync(string contentId, MediaSourceInfo source, CancellationToken cancellationToken)
    {
        if (_probed.TryGet(contentId, out var cached))
        {
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var tracks = await _probe.ProbeAsync(source, timeout.Token).ConfigureAwait(false);
            if (tracks.Streams.Count > 0)
            {
                _probed.Set(contentId, tracks);
            }

            return tracks;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Probing {ContentId} for its audio/subtitle tracks failed; playing it without a track list", contentId);
            return null;
        }
    }

    // The portal's own home-screen shelves are deliberately not listed: every one is hard-capped at 10 items
    // (measured live 2026-09-21 -- no endpoint pages deeper), while Descubrir browses the real, paginated catalog.
    // The root collages are the most general of all, so they pick last: Descubrir takes what the four catalogs' own
    // folders left over (about one title per catalog), TV en vivo what the live categories left.
    private async Task<IReadOnlyList<ChannelItemInfo>> RootFoldersAsync(PortalitoServices services, CancellationToken cancellationToken)
    {
        var catalogsTask = AllCatalogCollagesAsync(services, cancellationToken);
        var liveTask = LiveRootCollageAsync(services, cancellationToken);
        var catalogs = await catalogsTask.ConfigureAwait(false);
        var liveRoot = await liveTask.ConfigureAwait(false);

        var usedBelow = new HashSet<string>(catalogs.SelectMany(c => c.Used), StringComparer.Ordinal);
        var newestAcross = CollagePlanner.Interleave(catalogs.Select(c => c.Newest).ToList());
        // "Destacado" (the curated rows) leads; its tile is the freshest titles across catalogs the folders below
        // didn't already show, picked before the more general Descubrir tile -- through the same used set -- so the
        // two roots don't show the same posters either.
        var featured = CollagePlanner.Pick(newestAcross, usedBelow);
        var discover = CollagePlanner.Pick(newestAcross, usedBelow);
        var images = await Task.WhenAll(
            CollageAsync(services, "Destacado", CollageFit.Cover, featured, cancellationToken),
            CollageAsync(services, "Descubrir", CollageFit.Cover, discover, cancellationToken),
            CollageAsync(services, "TV en vivo", CollageFit.Contain, liveRoot, cancellationToken)).ConfigureAwait(false);
        return new[]
        {
            Folder(VodItemId.Featured().ToString(), "Destacado", ChannelFolderType.Container, images[0]),
            Folder(VodItemId.Discover().ToString(), "Descubrir", ChannelFolderType.Container, images[1]),
            Folder(VodItemId.LiveRoot().ToString(), "TV en vivo", ChannelFolderType.Container, images[2]),
        };
    }

    /// <summary>
    /// A folder thumbnail: <paramref name="label"/> over up to four images (see <see cref="CollageRenderer"/>), as the
    /// path of its rendered file (see <see cref="CollageService"/> for why a file); null if it can't be rendered.
    /// </summary>
    private async Task<string?> CollageAsync(PortalitoServices services, string label, CollageFit fit, IReadOnlyList<string> images, CancellationToken cancellationToken)
    {
        await _collageRenders.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _collages.FileAsync(label, fit, images.Take(CollagePlanner.PerCollage).ToList(), services.Proxy.OpenImageAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Collage for {Label} couldn't be rendered; the folder gets no thumbnail", label);
            return null;
        }
        finally
        {
            _collageRenders.Release();
        }
    }

    /// <summary>A folder's collage candidates, or none if the portal won't give them -- a thumbnail must not break a listing.</summary>
    private async Task<IReadOnlyList<CollageImage>> PostersOrNoneAsync(PortalitoServices services, string catalogCode, string? tag, int? year, CancellationToken cancellationToken)
    {
        await _thumbnailFetches.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await services.Catalogs.PostersAsync(services.Portal, catalogCode, tag, year, cancellationToken).ConfigureAwait(false);
        }
        catch (PortalException ex)
        {
            _logger.LogDebug("No collage posters for {Catalog}/{Tag}/{Year} ({Reason})", catalogCode, tag, year, ex.Message);
            return Array.Empty<CollageImage>();
        }
        finally
        {
            _thumbnailFetches.Release();
        }
    }

    /// <summary>
    /// One live category's channel logos as collage candidates, best first; none if the portal won't give them. Keyed
    /// by show: the portal lists the same show under several channels ("La Casa de los Famosos 1".."4",
    /// "El Chapulin Colorado T1-3"/"T4-7") with different logo URLs of the same picture.
    /// </summary>
    private async Task<IReadOnlyList<CollageImage>> LiveLogosAsync(PortalitoServices services, long columnId, CancellationToken cancellationToken)
    {
        var key = columnId.ToString(CultureInfo.InvariantCulture);
        if (_liveLogos.TryGet(key, out var cached))
        {
            return cached;
        }

        await _thumbnailFetches.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = (await services.Portal.LiveDataAsync(columnId, size: CatalogBrowser.CollageCandidates, cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
            var logos = LiveChannelList.Parse(payload)
                .Where(c => c.PosterUrl is not null)
                .Select(c => new CollageImage(CollageImage.KeyFor(c.Name), c.PosterUrl!))
                .DistinctBy(c => c.Url, StringComparer.Ordinal)
                .ToList();
            _liveLogos.Set(key, logos);
            return logos;
        }
        catch (PortalException ex)
        {
            _logger.LogDebug("No collage logos for live category {ColumnId} ({Reason})", columnId, ex.Message);
            return Array.Empty<CollageImage>();
        }
        finally
        {
            _thumbnailFetches.Release();
        }
    }

    private const string AllChannelsName = "Todos los canales";

    /// <summary>The live categories as the portal lists them, "Todos los canales" first when it doesn't; null if unavailable.</summary>
    private async Task<IReadOnlyList<(long Id, string Name)>?> LiveCategoryListAsync(PortalitoServices services, CancellationToken cancellationToken)
    {
        try
        {
            var payload = (await services.Portal.LiveCategoriesAsync(cancellationToken).ConfigureAwait(false)).Require();
            var listed = PortalJson.Objects(payload["recommendList"])
                .Select(c => (Id: long.TryParse(PortalJson.Str(c["columnId"]), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (long?)null, Name: PortalJson.NonBlank(c["name"])))
                .Where(c => c.Id is not null && c.Name is not null)
                // The portal names its all-channels category "ChannelList" (measured 2026-09-25), which isn't for viewers.
                .Select(c => (Id: c.Id!.Value, Name: c.Id == services.Portal.AllChannelsColumnId ? AllChannelsName : c.Name!))
                .ToList();
            if (listed.Count > 0 && !listed.Any(c => c.Id == services.Portal.AllChannelsColumnId))
            {
                listed.Insert(0, (services.Portal.AllChannelsColumnId, AllChannelsName));
            }

            return listed;
        }
        catch (PortalException ex)
        {
            _logger.LogWarning("Live categories unavailable, listing every channel instead ({Reason})", ex.Message);
            return null;
        }
    }

    /// <summary>The TV en vivo root's collage: all-channel logos the categories didn't take.</summary>
    private async Task<IReadOnlyList<string>> LiveRootCollageAsync(PortalitoServices services, CancellationToken cancellationToken)
    {
        var categories = await LiveCategoryListAsync(services, cancellationToken).ConfigureAwait(false) ?? Array.Empty<(long, string)>();
        return (await LiveCollagesAsync(services, categories, cancellationToken).ConfigureAwait(false)).Root;
    }

    /// <summary>
    /// TV en vivo's collages, most specific first: the countries/themes, then the all-channels category (general: it
    /// holds them all), then the TV en vivo root from what's left of all channels.
    /// </summary>
    private async Task<(IReadOnlyList<IReadOnlyList<string>> Logos, IReadOnlyList<string> Root)> LiveCollagesAsync(PortalitoServices services, IReadOnlyList<(long Id, string Name)> categories, CancellationToken cancellationToken)
    {
        var all = await LiveLogosAsync(services, services.Portal.AllChannelsColumnId, cancellationToken).ConfigureAwait(false);
        var candidates = await Task.WhenAll(categories.Select(c => c.Id == services.Portal.AllChannelsColumnId
            ? Task.FromResult(all)
            : LiveLogosAsync(services, c.Id, cancellationToken))).ConfigureAwait(false);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var specific = Enumerable.Range(0, categories.Count).Where(i => categories[i].Id != services.Portal.AllChannelsColumnId).ToList();
        var specificPicks = CollagePlanner.PickAll(specific.Select(i => candidates[i]).ToList(), used);
        var logos = new IReadOnlyList<string>[categories.Count];
        for (var k = 0; k < specific.Count; k++)
        {
            logos[specific[k]] = specificPicks[k];
        }

        for (var i = 0; i < categories.Count; i++)
        {
            logos[i] ??= CollagePlanner.Pick(candidates[i], used);
        }

        return (logos, CollagePlanner.Pick(all, used));
    }

    /// <summary>
    /// "TV en vivo": the live categories exactly as the portal serves them (<c>getNextColumns("live")</c> --
    /// countries like Colombia/México/Perú plus themes like Deportes or 24/7; the reference client's live menu reads the same list),
    /// in its order, with "Todos los canales" first when the portal doesn't list it. If the category list can't be
    /// had, falls back to every channel in one flat list rather than an empty folder.
    /// </summary>
    private async Task<IReadOnlyList<ChannelItemInfo>> LiveCategoryFoldersAsync(PortalitoServices services, CancellationToken cancellationToken)
    {
        var categories = await LiveCategoryListAsync(services, cancellationToken).ConfigureAwait(false);
        if (categories is null || categories.Count == 0)
        {
            return await LiveChannelsAsync(services, services.Portal.AllChannelsColumnId, cancellationToken).ConfigureAwait(false);
        }

        var live = await LiveCollagesAsync(services, categories, cancellationToken).ConfigureAwait(false);
        var images = await Task.WhenAll(categories.Select((c, i) => CollageAsync(services, c.Name, CollageFit.Contain, live.Logos[i], cancellationToken))).ConfigureAwait(false);
        return categories
            .Select((c, i) => Folder(VodItemId.LiveCategory(c.Id).ToString(), c.Name, ChannelFolderType.Container, images[i]))
            .ToList();
    }

    /// <summary>One live category's channels, as playable items (a channel code that doesn't fit the id charset is skipped).</summary>
    private static async Task<IReadOnlyList<ChannelItemInfo>> LiveChannelsAsync(PortalitoServices services, long columnId, CancellationToken cancellationToken)
    {
        var payload = (await services.Portal.LiveDataAsync(columnId, cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        return LiveChannelList.Parse(payload)
            .Where(c => VodItemId.IsSafePart(c.Code))
            .Select(c => new ChannelItemInfo
            {
                Id = VodItemId.LiveChannel(c.Code).ToString(),
                Name = c.Name,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                IsLiveStream = true,
                ImageUrl = c.PosterUrl is { } poster ? services.Signer.ImageUrl(poster, MediaSources.ImageUrlValidity) : null,
            })
            .ToList();
    }

    /// <summary>One catalog's collages, most specific first (see <see cref="CollagePlanner"/>).</summary>
    private sealed record CatalogCollages(
        IReadOnlyList<CollageImage> Newest,
        IReadOnlyList<IReadOnlyList<string>> Genres,
        IReadOnlyList<string> Todo,
        IReadOnlyList<string> Years,
        IReadOnlyList<string> Catalog,
        IReadOnlySet<string> Used);

    /// <summary>
    /// Genres pick first (the least-shared ones before "Animación"-like catch-alls), then "Todo", then "Por año",
    /// then the catalog's own tile in Descubrir -- each from its own candidates, skipping what a more specific folder
    /// took. "Por año" draws on the newest titles rather than its year folders: those would be ~37 more portal calls
    /// per catalog on every Descubrir open.
    /// </summary>
    private async Task<CatalogCollages> CatalogCollagesAsync(PortalitoServices services, int catalogIndex, CancellationToken cancellationToken)
    {
        var catalogCode = services.Catalogs.Entries[catalogIndex].Code;
        var vocabulary = await services.Catalogs.VocabularyAsync(services.Portal, cancellationToken).ConfigureAwait(false);
        var newestTask = PostersOrNoneAsync(services, catalogCode, null, null, cancellationToken);
        var byGenre = await Task.WhenAll(vocabulary.Tags.Select(t => PostersOrNoneAsync(services, catalogCode, t, null, cancellationToken))).ConfigureAwait(false);
        var newest = await newestTask.ConfigureAwait(false);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var genres = CollagePlanner.PickAll(byGenre, used);
        var todo = CollagePlanner.Pick(newest, used);
        var years = CollagePlanner.Pick(newest, used);
        var catalog = CollagePlanner.Pick(newest, used);
        return new CatalogCollages(newest, genres, todo, years, catalog, used);
    }

    /// <summary>Every catalog's collages; a catalog the portal won't plan (no filter vocabulary) gets name-only tiles.</summary>
    private Task<CatalogCollages[]> AllCatalogCollagesAsync(PortalitoServices services, CancellationToken cancellationToken)
        => Task.WhenAll(Enumerable.Range(0, services.Catalogs.Entries.Count).Select(async i =>
        {
            try
            {
                return await CatalogCollagesAsync(services, i, cancellationToken).ConfigureAwait(false);
            }
            catch (PortalException ex)
            {
                _logger.LogDebug("No collages for catalog {Catalog} ({Reason})", services.Catalogs.Entries[i].Code, ex.Message);
                var none = Array.Empty<string>();
                return new CatalogCollages(Array.Empty<CollageImage>(), Array.Empty<IReadOnlyList<string>>(), none, none, none, new HashSet<string>());
            }
        }));

    /// <summary>The "Descubrir" root: one folder per Portalito catalog (Películas/Series/Infantil/Anime), each a collage
    /// of that catalog's own titles not already shown by its genres.</summary>
    private async Task<IReadOnlyList<ChannelItemInfo>> DiscoverCatalogFoldersAsync(PortalitoServices services, CancellationToken cancellationToken)
    {
        var plans = await AllCatalogCollagesAsync(services, cancellationToken).ConfigureAwait(false);
        var images = await Task.WhenAll(services.Catalogs.Entries.Select((c, i) => CollageAsync(services, c.Label, CollageFit.Cover, plans[i].Catalog, cancellationToken))).ConfigureAwait(false);
        return services.Catalogs.Entries
            .Select((c, i) => Folder(VodItemId.Catalog(i).ToString(), c.Label, ChannelFolderType.Container, images[i]))
            .ToList();
    }

    /// <summary>One catalog's own folders: "Todo", one per genre, and "Por año" -- each a collage of its own titles.</summary>
    private async Task<IReadOnlyList<ChannelItemInfo>> CatalogFoldersAsync(PortalitoServices services, int catalogIndex, CancellationToken cancellationToken)
    {
        var vocabulary = await services.Catalogs.VocabularyAsync(services.Portal, cancellationToken).ConfigureAwait(false);
        var plan = await CatalogCollagesAsync(services, catalogIndex, cancellationToken).ConfigureAwait(false);

        var entries = new List<(string Id, string Label, IReadOnlyList<string> Images)> { (VodItemId.FilterAll(catalogIndex).ToString(), "Todo", plan.Todo) };
        for (var i = 0; i < vocabulary.Tags.Count; i++)
        {
            entries.Add((VodItemId.FilterGenre(catalogIndex, i).ToString(), GenreLabels.ToSpanish(vocabulary.Tags[i]), plan.Genres[i]));
        }

        entries.Add((VodItemId.FilterYears(catalogIndex).ToString(), "Por año", plan.Years));
        var images = await Task.WhenAll(entries.Select(e => CollageAsync(services, e.Label, CollageFit.Cover, e.Images, cancellationToken))).ConfigureAwait(false);
        return entries.Select((e, i) => Folder(e.Id, e.Label, ChannelFolderType.Container, images[i])).ToList();
    }

    /// <summary>
    /// A filtered listing: "years" lists one folder per year (from the shared filter vocabulary); anything else
    /// ("all", "g&lt;tag index&gt;", "y&lt;year&gt;") fetches that one filtered page from Portalito's own catalog and
    /// maps it -- movies play directly; series-type entries (one per season, e.g. "Reacher T4") are grouped by
    /// show so the listing shows one folder per show, not one per season (see <see cref="GroupCatalogItems"/>).
    /// </summary>
    private async Task<ChannelItemResult> FilterItemsAsync(PortalitoServices services, int catalogIndex, string filter, PageWindow window, CancellationToken cancellationToken)
    {
        if (filter == "years")
        {
            var yearsCatalogCode = services.Catalogs.Entries[catalogIndex].Code;
            var vocabulary = await services.Catalogs.VocabularyAsync(services.Portal, cancellationToken).ConfigureAwait(false);
            var candidates = await Task.WhenAll(vocabulary.Years.Select(y => PostersOrNoneAsync(services, yearsCatalogCode, null, y, cancellationToken))).ConfigureAwait(false);
            var byYear = CollagePlanner.PickAll(candidates, new HashSet<string>(StringComparer.Ordinal));
            var labels = vocabulary.Years.Select(y => y.ToString(CultureInfo.InvariantCulture)).ToList();
            var images = await Task.WhenAll(labels.Select((label, i) => CollageAsync(services, label, CollageFit.Cover, byYear[i], cancellationToken))).ConfigureAwait(false);
            var years = vocabulary.Years
                .Select((y, i) => Folder(VodItemId.FilterYear(catalogIndex, y).ToString(), labels[i], ChannelFolderType.Container, images[i]))
                .ToArray();
            return Slice(years, window);
        }

        string? tag = null;
        int? year = null;
        if (filter.Length > 1 && filter[0] == 'y' && int.TryParse(filter.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedYear))
        {
            year = parsedYear;
        }
        else if (filter.Length > 1 && filter[0] == 'g' && int.TryParse(filter.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var tagIndex))
        {
            var vocabulary = await services.Catalogs.VocabularyAsync(services.Portal, cancellationToken).ConfigureAwait(false);
            tag = tagIndex >= 0 && tagIndex < vocabulary.Tags.Count ? vocabulary.Tags[tagIndex] : null;
        }
        else if (filter != "all")
        {
            throw new ArgumentException($"Unrecognised filter selector '{filter}'.", nameof(filter));
        }

        var catalogCode = services.Catalogs.Entries[catalogIndex].Code;
        // A year folder lists the whole year (and is what the search index walks); "Todo" and genres stay at the
        // newest 200 -- the whole catalog in one folder would be tens of thousands of items.
        var content = year is not null
            ? await services.Catalogs.ListAllAsync(services.Portal, catalogCode, tag, year, cancellationToken).ConfigureAwait(false)
            : await services.Catalogs.ListAsync(services.Portal, catalogCode, tag, year, cancellationToken).ConfigureAwait(false);
        return await MapListingAsync(services, content, window, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps raw portal items to playable movies and per-show folders, fills blanks from TMDB, and pages.</summary>
    private async Task<ChannelItemResult> MapListingAsync(PortalitoServices services, IReadOnlyList<JsonObject> content, PageWindow window, CancellationToken cancellationToken)
    {
        var grouped = GroupCatalogItems(content, services.Signer, _runtimes);
        if (services.Tmdb is { } tmdb)
        {
            await FillBlanksFromTmdbAsync(tmdb, grouped, cancellationToken).ConfigureAwait(false);
        }

        return Slice(grouped.Select(g => g.Item).ToList(), window);
    }

    /// <summary>
    /// The "Destacado" root's curated rows: Estrenos of the newest year (Películas, Series, Anime del momento) and
    /// Mejor valoradas (Películas, Series, Anime). Each is a folder that opens to a grid -- Jellyfin channels can't
    /// draw horizontal rails. The score-sorted "Mejor valoradas" listings aren't fetched here (that's several portal
    /// pages); the row's own collage uses the catalog's newest posters, and the ranking happens when it's opened.
    /// </summary>
    private async Task<IReadOnlyList<ChannelItemInfo>> DestacadoFoldersAsync(PortalitoServices services, CancellationToken cancellationToken)
    {
        if (services.Discovery is { } discovery)
        {
            return await TmdbRowFoldersAsync(services, discovery, cancellationToken).ConfigureAwait(false);
        }

        var vocabulary = await services.Catalogs.VocabularyAsync(services.Portal, cancellationToken).ConfigureAwait(false);
        var year = vocabulary.Years.DefaultIfEmpty(DateTime.UtcNow.Year).Max();

        // (catalog index, mode, label). Anime uses friendlier labels than "Estrenos {year} · Anime". Only the rows whose
        // catalog is configured: these assume the movies/series/kids/anime order, and with fewer than four catalogs the
        // anime rows used to throw and take the whole Destacado folder down.
        var rows = new[]
        {
            (Catalog: 0, Mode: "estrenos", Label: $"Estrenos {year} · Películas"),
            (Catalog: 1, Mode: "estrenos", Label: $"Estrenos {year} · Series"),
            (Catalog: 3, Mode: "estrenos", Label: "Anime del momento"),
            (Catalog: 0, Mode: "top", Label: "Películas mejor valoradas"),
            (Catalog: 1, Mode: "top", Label: "Series mejor valoradas"),
            (Catalog: 3, Mode: "top", Label: "Anime mejor valorado"),
        }.Where(r => r.Catalog < services.Catalogs.Entries.Count).ToArray();

        // Collage posters: an "estrenos" row shows its own newest-year titles; a "top" row uses the catalog's newest
        // (the real ranking is deferred to opening the row), both one small cached call. Those overlap heavily (this
        // year's titles ARE the catalog's newest), so the rows are planned together like any sibling folders: each
        // takes titles the others haven't, and a show counts once within a collage.
        var posters = await Task.WhenAll(rows.Select(r =>
            PostersOrNoneAsync(services, services.Catalogs.Entries[r.Catalog].Code, null, r.Mode == "estrenos" ? year : (int?)null, cancellationToken))).ConfigureAwait(false);
        var picks = CollagePlanner.PickAll(posters, new HashSet<string>(StringComparer.Ordinal));
        var images = await Task.WhenAll(rows.Select((r, i) =>
            CollageAsync(services, r.Label, CollageFit.Cover, picks[i], cancellationToken))).ConfigureAwait(false);

        return rows.Select((r, i) => Folder(VodItemId.Row(r.Catalog, r.Mode).ToString(), r.Label, ChannelFolderType.Container, images[i])).ToList();
    }

    /// <summary>
    /// The configured TMDB rows as "Destacado" folders. Their collages use the TMDB list's own posters (cheap: no portal
    /// search needed just for a thumbnail); opening a row is what reconciles it with the portal.
    /// </summary>
    private async Task<IReadOnlyList<ChannelItemInfo>> TmdbRowFoldersAsync(PortalitoServices services, DiscoveryBrowser discovery, CancellationToken cancellationToken)
    {
        var entries = await Task.WhenAll(discovery.Rows.Select((_, i) => TmdbEntriesOrNoneAsync(discovery, i, cancellationToken))).ConfigureAwait(false);
        var posters = entries
            .Select(list => (IReadOnlyList<CollageImage>)list
                .Where(e => e.PosterUrl is not null)
                .Select(e => new CollageImage(CollageImage.KeyFor(e.Title ?? e.EnglishTitle ?? e.Id), e.PosterUrl!))
                .ToList())
            .ToList();
        var picks = CollagePlanner.PickAll(posters, new HashSet<string>(StringComparer.Ordinal));
        var images = await Task.WhenAll(discovery.Rows.Select((r, i) =>
            CollageAsync(services, r.Label, CollageFit.Cover, picks[i], cancellationToken))).ConfigureAwait(false);

        return discovery.Rows.Select((r, i) => Folder(VodItemId.Row(DiscoveryBrowser.RowKey(r.Label), TmdbRowMode).ToString(), r.Label, ChannelFolderType.Container, images[i])).ToList();
    }

    /// <summary>A row's TMDB list, or none if TMDB won't answer -- a thumbnail must not break the listing.</summary>
    private async Task<IReadOnlyList<TmdbEntry>> TmdbEntriesOrNoneAsync(DiscoveryBrowser discovery, int rowIndex, CancellationToken cancellationToken)
    {
        try
        {
            return await discovery.EntriesAsync(rowIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug("No TMDB list for row {Row} ({Reason})", discovery.Rows[rowIndex].Label, ex.Message);
            return Array.Empty<TmdbEntry>();
        }
    }

    /// <summary>A TMDB row's portal titles; empty (not an error) when the row no longer exists or TMDB is unreachable.</summary>
    private async Task<ChannelItemResult> TmdbRowItemsAsync(PortalitoServices services, int rowKey, PageWindow window, CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonObject> content = Array.Empty<JsonObject>();
        if (services.Discovery is { } discovery && discovery.IndexOfRow(rowKey) is >= 0 and var rowIndex)
        {
            try
            {
                content = await discovery.RowAsync(services.Portal, rowIndex, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning("TMDB row {Row} couldn't be built ({Reason})", discovery.Rows[rowIndex].Label, ex.Message);
            }
        }

        return await MapListingAsync(services, content, window, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One curated row's titles: "estrenos" = the newest of the newest year; "top" = the best-rated recent.</summary>
    private async Task<ChannelItemResult> RowItemsAsync(PortalitoServices services, int catalogIndex, string mode, PageWindow window, CancellationToken cancellationToken)
    {
        var code = services.Catalogs.Entries[catalogIndex].Code;
        IReadOnlyList<JsonObject> content;
        if (mode == "top")
        {
            content = await services.Catalogs.TopRatedAsync(services.Portal, code, cancellationToken).ConfigureAwait(false);
        }
        else if (mode == "estrenos")
        {
            var vocabulary = await services.Catalogs.VocabularyAsync(services.Portal, cancellationToken).ConfigureAwait(false);
            var year = vocabulary.Years.DefaultIfEmpty(DateTime.UtcNow.Year).Max();
            content = await services.Catalogs.ListAsync(services.Portal, code, tag: null, year: year, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException($"Unrecognised row mode '{mode}'.", nameof(mode));
        }

        return await MapListingAsync(services, content, window, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills a listing's blank synopses and missing posters from TMDB, never touching a field Portalito did send. Bounded:
    /// at most <see cref="TmdbConcurrency"/> lookups at a time and <see cref="TmdbBudget"/> per listing -- whatever
    /// isn't looked up in time stays blank for now (TMDB answers are cached, so the next listing gets further).
    /// </summary>
    private async Task FillBlanksFromTmdbAsync(TmdbClient tmdb, IReadOnlyList<(ChannelItemInfo Item, TitleQuery Query)> listing, CancellationToken cancellationToken)
    {
        // Only a blank synopsis or missing poster is worth a lookup (the portal leaves those blank most often). Year
        // and rating are filled opportunistically when we're already looking one up, never on their own -- the portal
        // omits score constantly, and triggering a lookup for that alone would blow the budget.
        var wanting = listing.Where(g => g.Item.Overview is null || g.Item.ImageUrl is null).ToList();
        if (wanting.Count == 0)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TmdbBudget);
        using var slots = new SemaphoreSlim(TmdbConcurrency);
        var failures = 0;
        await Task.WhenAll(wanting.Select(async g =>
        {
            try
            {
                await slots.WaitAsync(budget.Token).ConfigureAwait(false);
                try
                {
                    if (await tmdb.LookupAsync(g.Query, budget.Token).ConfigureAwait(false) is { } info)
                    {
                        g.Item.Overview ??= info.Overview;
                        g.Item.ImageUrl ??= info.PosterUrl;
                        g.Item.ProductionYear ??= info.Year;
                        g.Item.CommunityRating ??= (float?)info.Rating;
                    }
                }
                finally
                {
                    slots.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Out of budget: this one stays blank for now.
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                Interlocked.Increment(ref failures);
            }
        })).ConfigureAwait(false);

        if (failures > 0)
        {
            _logger.LogWarning("TMDB fallback: {Failures} of {Count} lookups failed", failures, wanting.Count);
        }
    }

    /// <summary>
    /// Movies map straight to a playable item; series-type entries (one raw item per season) are grouped by
    /// <see cref="SeasonGrouping.WithoutSeason"/> so the listing shows one folder per SHOW, in first-seen (i.e.
    /// portal, newest-first) order -- not one per season. The show folder's own seasons are resolved separately,
    /// from the authoritative <c>sameSeasonSeriesList</c> (see <see cref="ShowSeasonsAsync"/>), not from whatever
    /// happened to appear in this one filtered/paged listing.
    /// </summary>
    private static IReadOnlyList<(ChannelItemInfo Item, TitleQuery Query)> GroupCatalogItems(IReadOnlyList<JsonObject> content, ProxyUrlSigner signer, RuntimeStore runtimes)
    {
        var items = new List<(ChannelItemInfo Item, TitleQuery Query)>();
        var seenShows = new HashSet<string>();
        foreach (var c in content)
        {
            var contentId = PortalJson.Str(c["contentId"]);
            if (!VodItemId.IsSafePart(contentId))
            {
                continue;
            }

            var name = PortalJson.Str(c["name"]) ?? contentId!;
            var programType = PortalJson.Str(c["programType"]) ?? string.Empty;
            // keyWords carries the IMDb id on the payloads that include it (reliably on v4/getItemData detail; the
            // filterByContent listing may omit it, in which case ImdbId is null and TMDB matching falls back to title).
            var imdb = PortalJson.ImdbId(c["keyWords"]);
            if (!SeriesTypes.Contains(programType))
            {
                var movie = new ChannelItemInfo
                {
                    Id = VodItemId.Movie(contentId!).ToString(),
                    Name = name,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Movie,
                };
                ApplyMetadata(movie, c, signer);
                ApplyRuntime(movie, contentId!, c, runtimes);
                items.Add((movie, new TitleQuery(name, PortalJson.NonBlank(c["alias"]), movie.ProductionYear, IsSeries: false, imdb)));
                continue;
            }

            var groupKey = SeasonGrouping.WithoutSeason(name);
            if (!seenShows.Add(groupKey))
            {
                continue;
            }

            var show = Folder(VodItemId.Show(contentId!).ToString(), SeasonGrouping.StripSeasonForDisplay(name), ChannelFolderType.Series);
            ApplyMetadata(show, c, signer);

            // A season's name and alias carry its season marker ("... T2", "... S2"); TMDB knows the show.
            var alias = PortalJson.NonBlank(c["alias"]) is { } a ? SeasonGrouping.StripSeasonForDisplay(a) : null;
            items.Add((show, new TitleQuery(show.Name, alias, show.ProductionYear, IsSeries: true, imdb)));
        }

        return items;
    }

    /// <summary>
    /// A show's seasons, from the authoritative <c>sameSeasonSeriesList</c> (every season's own contentId --
    /// measured live 2026-09-23) rather than from whichever seasons happened to appear in a filtered listing.
    /// Each season is a <see cref="VodItemKind.ShowSeason"/> folder listing its episodes.
    /// </summary>
    private static async Task<ChannelItemResult> ShowSeasonsAsync(PortalitoServices services, string contentId, PageWindow window, CancellationToken cancellationToken)
    {
        var detail = (await services.Portal.DetailAsync(contentId, type: "0", cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var asset = detail["assetData"] as JsonObject ?? detail;
        var seasons = PortalJson.Objects(asset["sameSeasonSeriesList"])
            .Select(s => (ContentId: PortalJson.Str(s["contentId"]), SeasonNumber: PortalJson.Int(s["seasonNumber"])))
            .Where(s => VodItemId.IsSafePart(s.ContentId))
            .OrderBy(s => s.SeasonNumber ?? int.MaxValue)
            .ToList();

        if (seasons.Count == 0)
        {
            // No sameSeasonSeriesList (a single-season show, or the field is simply absent): fall back to this
            // content id being the only season, same as a native series folder.
            seasons.Add((contentId, 1));
        }

        var folders = seasons
            .Select(s => Folder(
                VodItemId.ShowSeason(s.ContentId!).ToString(),
                s.SeasonNumber is { } n ? $"Temporada {n}" : "Temporada",
                ChannelFolderType.Season))
            .ToList();
        for (var i = 0; i < folders.Count; i++)
        {
            folders[i].IndexNumber = seasons[i].SeasonNumber;
        }

        return Slice(folders, window);
    }

    private static int ParseCatalogIndex(string primary)
        => int.Parse(primary, NumberStyles.None, CultureInfo.InvariantCulture);

    private static bool HasCatalog(PortalitoServices services, string primary)
        => int.TryParse(primary, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < services.Catalogs.Entries.Count;

    private static ChannelItemInfo? EpisodeItem(ProxyUrlSigner signer, string seriesId, JsonObject episode, SeasonListing season, RuntimeStore runtimes)
    {
        var episodeId = PortalJson.Str(episode["contentId"]);
        if (!VodItemId.IsSafePart(episodeId))
        {
            return null;
        }

        var item = new ChannelItemInfo
        {
            Id = VodItemId.Episode(seriesId, episodeId!).ToString(),
            Name = PortalJson.Str(episode["name"]) ?? episodeId,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Episode,
            IndexNumber = PortalJson.Int(episode["seriesNumber"]),

            // Next Up and episode ordering need the season; Jellyfin only reads these when it first creates the item,
            // so items created before this was set are filled in by SeriesRepairTask.
            ParentIndexNumber = season.SeasonNumber,
            SeriesName = season.ShowName,
        };
        ApplyMetadata(item, episode, signer);
        ApplyRuntime(item, episodeId!, episode, runtimes);
        return item;
    }

    /// <summary>
    /// Gives a movie/episode its runtime, so Jellyfin keeps playback positions (without one it marks the title Played
    /// and drops the position: UserDataManager.UpdatePlayState). Known from an earlier playback's probe, or from the
    /// portal's <c>duration</c> when it sends one (rarely). <c>DateModified</c> = when the runtime was learned: a newer
    /// value is what makes Jellyfin save a changed runtime on an item it already has (ChannelManager forceUpdate).
    /// </summary>
    private static void ApplyRuntime(ChannelItemInfo item, string contentId, JsonObject content, RuntimeStore runtimes)
    {
        if (!runtimes.TryGet(contentId, out var known)
            && RuntimeStore.ParseDuration(PortalJson.Str(content["duration"])) is { } portalTicks
            && runtimes.Set(contentId, portalTicks, DateTime.UtcNow))
        {
            runtimes.TryGet(contentId, out known);
        }

        if (known is not null)
        {
            item.RunTimeTicks = known.Ticks;
            item.DateModified = known.LearnedUtc;
        }
    }

    /// <summary>
    /// Fills in what the portal already sends on every content/episode payload but was previously left blank:
    /// synopsis, year, rating, genres and cast. Absent/blank fields are left null rather than set to an empty
    /// placeholder. <c>description</c> is often empty on the portal's own data (measured live 2026-09-23).
    /// </summary>
    private static void ApplyMetadata(ChannelItemInfo item, JsonObject content, ProxyUrlSigner signer)
    {
        item.ImageUrl = PortalJson.PosterUrl(content) is { } poster ? signer.ImageUrl(poster, MediaSources.ImageUrlValidity) : null;
        item.Overview = PortalJson.NonBlank(content["description"]);
        item.OriginalTitle = PortalJson.NonBlank(content["alias"]);
        item.ProductionYear = PortalJson.Year(content["releaseTime"]);
        if (PortalJson.Str(content["releaseTime"]) is { } releaseTime && DateTime.TryParse(releaseTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var premiere))
        {
            item.PremiereDate = premiere;
        }

        if (PortalJson.Str(content["score"]) is { } scoreText && double.TryParse(scoreText, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) && score > 0)
        {
            item.CommunityRating = (float)score;
        }

        var tags = PortalJson.CommaList(content["tags"]);
        if (tags.Count > 0)
        {
            item.Genres = tags.Select(GenreLabels.ToSpanish).ToList();
        }

        var people = new List<PersonInfo>();
        AddPeople(people, PortalJson.NonBlank(content["director"]), PersonKind.Director);
        AddPeople(people, PortalJson.NonBlank(content["actorDisplay"]), PersonKind.Actor);
        if (people.Count > 0)
        {
            item.People = people;
        }
    }

    private static void AddPeople(List<PersonInfo> people, string? commaSeparatedNames, PersonKind kind)
    {
        if (commaSeparatedNames is null)
        {
            return;
        }

        foreach (var name in commaSeparatedNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            people.Add(new PersonInfo { Name = name, Type = kind });
        }
    }

    private static ChannelItemInfo Folder(string id, string name, ChannelFolderType folderType, string? imageUrl = null) => new()
    {
        Id = id,
        Name = name,
        Type = ChannelItemType.Folder,
        FolderType = folderType,
        ImageUrl = imageUrl,
    };

    private static ChannelItemResult Slice(IReadOnlyList<ChannelItemInfo> all, PageWindow window)
        => new()
        {
            Items = all.Skip(window.Start).Take(window.Limit).ToList(),
            TotalRecordCount = all.Count,
        };
}
