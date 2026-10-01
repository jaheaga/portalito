using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Metadata;
using Jellyfin.Plugin.Portalito.Portal;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Keeps the hidden "Portalito · Siguiendo" library in step with what people watch, so Portalito series reach Jellyfin's
/// Next Up (which only reads libraries -- a channel episode never qualifies). Each run:
/// <list type="number">
/// <item>finds the shows someone follows: played an episode of in the last 60 days (in the channel or the library),
/// or marked favorite (<see cref="FollowPlanner"/>);</item>
/// <item>writes each one's .strm/.nfo files (refetching its episode list every <see cref="RefreshEvery"/>) and deletes
/// the ones nobody follows any more;</item>
/// <item>scans just this library when files changed;</item>
/// <item>copies each user's progress from channel episodes to their library copies (<see cref="WatchState"/>);</item>
/// <item>keeps the library hidden from menus and readable by whoever sees the channel (<see cref="FollowLibrary"/>).</item>
/// </list>
/// Runs at startup, every 30 minutes, and shortly after someone stops a channel episode (<see cref="FollowTrigger"/>).
/// </summary>
public sealed class FollowSyncTask : IScheduledTask
{
    /// <summary>How often a followed show's episode list is refetched from the portal (new episodes, new seasons).</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(12);

    private readonly IChannelManager _channels;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IApplicationPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IPortalitoServicesProvider _services;
    private readonly RuntimeStore _runtimes;
    private readonly FollowStore _store;
    private readonly ILogger<FollowSyncTask> _logger;

    // A season's show (the canonical show id), for seasons of shows not followed yet: one portal call each, once per run of Jellyfin.
    private readonly Dictionary<string, string> _showOfSeason = new(StringComparer.Ordinal);

    public FollowSyncTask(
        IChannelManager channels,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        IApplicationPaths paths,
        IFileSystem fileSystem,
        IPortalitoServicesProvider services,
        RuntimeStore runtimes,
        FollowStore store,
        ILogger<FollowSyncTask> logger)
    {
        _channels = channels;
        _library = library;
        _users = users;
        _userData = userData;
        _paths = paths;
        _fileSystem = fileSystem;
        _services = services;
        _runtimes = runtimes;
        _store = store;
        _logger = logger;
    }

    public string Name => "Sincronizar Portalito · Siguiendo";

    public string Key => "PortalitoFollowSync";

    public string Description => "Refleja las series de Portalito que se están viendo en la biblioteca oculta \"Portalito · Siguiendo\", para que aparezcan en \"A continuación\".";

    public string Category => "Portalito";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks },
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is not { FollowLibraryEnabled: true } config)
        {
            _logger.LogDebug("Siguiendo: turned off in the plugin settings");
            return;
        }

        PortalitoServices services;
        try
        {
            services = _services.Get();
        }
        catch (PortalException ex)
        {
            _logger.LogInformation("Siguiendo: the plugin isn't configured yet ({Reason})", ex.Message);
            return;
        }

        var channels = await _channels.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        if (channels.Items.FirstOrDefault(c => c.Name == "Portalito VOD") is not { } channel)
        {
            _logger.LogInformation("Siguiendo: the Portalito VOD channel isn't registered yet");
            return;
        }

        var root = FollowLibrary.Root(config, _paths.DataPath);
        var host = new FollowLibrary(_library, _users, _logger);
        Directory.CreateDirectory(root);

        // An existing library is hidden (and shared) first, so it's readable before its episodes are queried per user.
        var library = host.Find(root);
        var usersChanged = library is null ? 0 : await host.HideAndShareAsync(library.Id, channel.Id).ConfigureAwait(false);
        progress.Report(5);
        var users = FollowLibrary.AllUsers(_users);
        var (activity, channelPlays) = await ActivityAsync(services, users, channel.Id, library?.Id, cancellationToken).ConfigureAwait(false);
        var follow = FollowPlanner.Plan(activity, DateTime.UtcNow);
        progress.Report(20);

        var writer = new FollowWriter(root, (url, ct) => DownloadAsync(services, url, ct));
        var changed = false;
        foreach (var gone in _store.All.Where(s => !follow.Contains(s.ShowId)).ToList())
        {
            _logger.LogInformation("Siguiendo: {Show} is no longer followed; removing it", gone.Name);
            changed |= writer.Delete(gone.Folder);
            _store.Remove(gone.ShowId);
        }

        for (var i = 0; i < follow.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed |= await MirrorAsync(services, writer, root, follow[i], cancellationToken).ConfigureAwait(false);
            progress.Report(20 + (60.0 * (i + 1) / follow.Count));
        }

        // Jellyfin skips an empty library folder ("is inaccessible or empty, skipping") and never gives it an item, so the
        // library is created -- or, if it was created empty, finally indexed -- only once it has a show in it.
        if (library is null)
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
            {
                _logger.LogInformation("Siguiendo: nobody follows a Portalito series yet; nothing to mirror");
                return;
            }

            if ((library = await host.EnsureAsync(root).ConfigureAwait(false)) is null)
            {
                _logger.LogWarning("Siguiendo: the library at {Root} couldn't be created", root);
                return;
            }

            usersChanged = await host.HideAndShareAsync(library.Id, channel.Id).ConfigureAwait(false);
            changed = true;
        }

        // Also when files are there but Jellyfin hasn't indexed them (a scan that was cut short, or a library created by
        // an earlier version): fewer series in the library than shows followed.
        var indexed = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, AncestorIds = new[] { library.Id } }).Count;
        if (changed || indexed < _store.All.Count)
        {
            await host.ScanAsync(library, new DirectoryService(_fileSystem), cancellationToken).ConfigureAwait(false);
        }

        progress.Report(90);
        var copied = CopyWatchState(library, channelPlays, cancellationToken);
        _logger.LogInformation(
            "Siguiendo: {Shows} shows followed{Changed}, {Copied} episode states copied from the channel, {Users} users updated",
            follow.Count,
            changed ? " (files updated)" : string.Empty,
            copied,
            usersChanged);
        progress.Report(100);
    }

    /// <summary>A user's progress on one channel episode (<see cref="EpisodeId"/> is the portal's contentId).</summary>
    private sealed record ChannelPlay(User User, BaseItem Item, UserItemData Data, string EpisodeId);

    /// <summary>Who follows which show, read from every user's plays and favorites in the channel and in the library.</summary>
    private async Task<(List<ShowActivity> Activity, List<ChannelPlay> ChannelPlays)> ActivityAsync(
        PortalitoServices services,
        IReadOnlyList<User> users,
        Guid channelId,
        Guid? libraryId,
        CancellationToken cancellationToken)
    {
        var activity = new List<ShowActivity>();
        var plays = new List<ChannelPlay>();
        foreach (var user in users)
        {
            // Channel episodes: an id "epi:<season>:<episode>" names the season, and through it the show.
            foreach (var item in Played(user, new InternalItemsQuery(user) { ChannelIds = new[] { channelId }, IncludeItemTypes = new[] { BaseItemKind.Episode } }))
            {
                if (VodItemId.TryParse(item.ExternalId, out var id) && id.Kind == VodItemKind.Episode
                    && _userData.GetUserData(user, item) is { } data
                    && await ShowOfSeasonAsync(services, id.Primary, cancellationToken).ConfigureAwait(false) is { } showId)
                {
                    plays.Add(new ChannelPlay(user, item, data, id.Secondary!));
                    activity.Add(new ShowActivity(showId, data.LastPlayedDate));
                }
            }

            // A show marked favorite in the channel ("shw:<season>") is followed even before anyone plays it.
            var favorites = _library.GetItemList(new InternalItemsQuery(user) { ChannelIds = new[] { channelId }, IsFavorite = true, IncludeItemTypes = new[] { BaseItemKind.Series } });
            foreach (var item in favorites)
            {
                if (VodItemId.TryParse(item.ExternalId, out var id) && id.Kind == VodItemKind.Show
                    && await ShowOfSeasonAsync(services, id.Primary, cancellationToken).ConfigureAwait(false) is { } showId)
                {
                    activity.Add(new ShowActivity(showId, null, Favorite: true));
                }
            }

            // The library's own episodes and series: what keeps a show followed once it's watched there.
            if (libraryId is not { } inLibrary)
            {
                continue;
            }

            foreach (var item in Played(user, new InternalItemsQuery(user) { IncludeItemTypes = new[] { BaseItemKind.Episode }, AncestorIds = new[] { inLibrary } }))
            {
                if (ShowOfPath(item.Path) is { } show && _userData.GetUserData(user, item) is { } data)
                {
                    activity.Add(new ShowActivity(show.ShowId, data.LastPlayedDate));
                }
            }

            foreach (var item in _library.GetItemList(new InternalItemsQuery(user) { IsFavorite = true, IncludeItemTypes = new[] { BaseItemKind.Series }, AncestorIds = new[] { inLibrary } }))
            {
                if (ShowOfPath(item.Path) is { } show)
                {
                    activity.Add(new ShowActivity(show.ShowId, null, Favorite: true));
                }
            }
        }

        return (activity, plays);
    }

    /// <summary>The query's items the user played or started (two queries: played and resumable), each once.</summary>
    private IEnumerable<BaseItem> Played(User user, InternalItemsQuery query)
    {
        query.IsPlayed = true;
        var played = _library.GetItemList(query);
        query.IsPlayed = null;
        query.IsResumable = true;
        return played.Concat(_library.GetItemList(query)).DistinctBy(i => i.Id);
    }

    /// <summary>The followed show whose folder holds <paramref name="path"/> (an episode's file or a show's folder).</summary>
    private FollowedShow? ShowOfPath(string? path)
    {
        for (var dir = path; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (_store.ShowForFolder(Path.GetFileName(dir)) is { } show)
            {
                return show;
            }
        }

        return null;
    }

    /// <summary>
    /// The canonical id of the show a season belongs to: the contentId of its lowest-numbered season, from the season's
    /// own <c>sameSeasonSeriesList</c>. Null when the portal can't say right now (that show is skipped this run).
    /// </summary>
    private async Task<string?> ShowOfSeasonAsync(PortalitoServices services, string seasonId, CancellationToken cancellationToken)
    {
        if (_store.ShowForSeason(seasonId) is { } followed)
        {
            return followed.ShowId;
        }

        if (_showOfSeason.TryGetValue(seasonId, out var cached))
        {
            return cached;
        }

        try
        {
            var seasons = await SeasonsAsync(services, seasonId, cancellationToken).ConfigureAwait(false);
            var showId = seasons[0].ContentId;
            foreach (var season in seasons)
            {
                _showOfSeason[season.ContentId] = showId;
            }

            _showOfSeason[seasonId] = showId;
            return showId;
        }
        catch (PortalException ex)
        {
            _logger.LogWarning("Siguiendo: couldn't tell which show season {Season} belongs to ({Reason})", seasonId, ex.Message);
            return null;
        }
    }

    /// <summary>A show's seasons, lowest number first (the first one's contentId is the show's id); just the given one when the portal lists none.</summary>
    private static async Task<IReadOnlyList<(string ContentId, int? Number)>> SeasonsAsync(PortalitoServices services, string contentId, CancellationToken cancellationToken)
    {
        var detail = (await services.Portal.DetailAsync(contentId, type: "0", cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var asset = detail["assetData"] as JsonObject ?? detail;
        var seasons = PortalJson.Objects(asset["sameSeasonSeriesList"])
            .Select(s => (ContentId: PortalJson.Str(s["contentId"]), Number: PortalJson.Int(s["seasonNumber"])))
            .Where(s => VodItemId.IsSafePart(s.ContentId))
            .Select(s => (ContentId: s.ContentId!, s.Number))
            .DistinctBy(s => s.ContentId)
            .OrderBy(s => s.Number ?? int.MaxValue)
            .ThenBy(s => s.ContentId, StringComparer.Ordinal)
            .ToList();
        return seasons.Count > 0 ? seasons : new[] { (contentId, (int?)null) };
    }

    /// <summary>Writes (or refreshes) one followed show's files; returns whether any file changed.</summary>
    private async Task<bool> MirrorAsync(PortalitoServices services, FollowWriter writer, string root, string showId, CancellationToken cancellationToken)
    {
        var known = _store.Get(showId);
        if (known is not null && DateTime.UtcNow - known.RefreshedUtc < RefreshEvery && Directory.Exists(Path.Combine(root, known.Folder)))
        {
            return false;
        }

        try
        {
            var (show, seasonIds) = await BuildShowAsync(services, showId, cancellationToken).ConfigureAwait(false);
            if (show.Episodes.Count == 0)
            {
                _logger.LogWarning("Siguiendo: the portal lists no episodes for {Show} right now; leaving it as it was", show.Name);
                return false;
            }

            var changed = await writer.WriteAsync(show, services.Signer.PlayUrl, known?.Folder, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            _store.Set(new FollowedShow(showId, show.Name, FollowFiles.ShowFolder(show.Name, showId), seasonIds, known?.AddedUtc ?? now, now));
            if (known is null)
            {
                _logger.LogInformation("Siguiendo: now following {Show} ({Episodes} episodes)", show.Name, show.Episodes.Count);
            }

            return changed;
        }
        catch (Exception ex) when (ex is PortalException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Siguiendo: couldn't mirror show {Show}; trying again on the next run", known?.Name ?? showId);
            return false;
        }
    }

    /// <summary>A show as the library files describe it: its metadata from the first season, and every season's episodes.</summary>
    private async Task<(MirrorShow Show, IReadOnlyList<string> SeasonIds)> BuildShowAsync(PortalitoServices services, string showId, CancellationToken cancellationToken)
    {
        var detail = (await services.Portal.DetailAsync(showId, type: "0", cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var asset = detail["assetData"] as JsonObject ?? detail;
        var seasons = await SeasonsAsync(services, showId, cancellationToken).ConfigureAwait(false);
        var name = SeasonGrouping.StripSeasonForDisplay(PortalJson.NonBlank(asset["name"]) ?? showId);
        if (name.Length == 0)
        {
            name = showId;
        }

        var poster = PortalJson.PosterUrl(asset);
        var plot = PortalJson.NonBlank(asset["description"]);
        var year = PortalJson.Year(asset["releaseTime"]);
        if ((poster is null || plot is null) && services.Tmdb is { } tmdb)
        {
            var alias = PortalJson.NonBlank(asset["alias"]) is { } a ? SeasonGrouping.StripSeasonForDisplay(a) : null;
            try
            {
                if (await tmdb.LookupAsync(new TitleQuery(name, alias, year, IsSeries: true, PortalJson.ImdbId(asset["keyWords"])), cancellationToken).ConfigureAwait(false) is { } info)
                {
                    poster ??= info.PosterUrl;
                    plot ??= info.Overview;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogDebug("Siguiendo: no TMDB fallback for {Show} ({Reason})", name, ex.Message);
            }
        }

        var episodes = new List<MirrorEpisode>();
        var taken = new HashSet<(int, int)>();
        foreach (var (seasonId, _) in seasons)
        {
            var listing = await services.Portal.SeasonAsync(seasonId, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < listing.Episodes.Count; i++)
            {
                var e = listing.Episodes[i];
                var episodeId = PortalJson.Str(e["contentId"]);
                var number = PortalJson.Int(e["seriesNumber"]) is > 0 and var n ? n : i + 1;
                if (!VodItemId.IsSafePart(episodeId) || !taken.Add((listing.SeasonNumber, number)))
                {
                    continue;
                }

                long? runtime = _runtimes.TryGet(episodeId!, out var knownRuntime) ? knownRuntime.Ticks : RuntimeStore.ParseDuration(PortalJson.Str(e["duration"]));
                DateTime? aired = PortalJson.Str(e["releaseTime"]) is { } released && DateTime.TryParse(released, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
                episodes.Add(new MirrorEpisode(
                    seasonId,
                    episodeId!,
                    listing.SeasonNumber,
                    number,
                    FollowFiles.EpisodeTitle(PortalJson.Str(e["name"]), listing.ShowName ?? name, number),
                    PortalJson.NonBlank(e["description"]),
                    runtime,
                    PortalJson.PosterUrl(e),
                    aired));
            }
        }

        var genres = PortalJson.CommaList(asset["tags"]).Select(GenreLabels.ToSpanish).ToList();
        return (new MirrorShow(showId, name, episodes, plot, year, genres, poster), seasons.Select(s => s.ContentId).ToList());
    }

    private static async Task<byte[]?> DownloadAsync(PortalitoServices services, string url, CancellationToken cancellationToken)
    {
        using var response = await services.Proxy.OpenImageAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Copies each user's newer channel progress onto the episode's library copy, and then clears the channel copy's
    /// resume point (its "played" mark stays) so Continue Watching doesn't list the same episode twice.
    /// </summary>
    private int CopyWatchState(Folder library, IReadOnlyList<ChannelPlay> plays, CancellationToken cancellationToken)
    {
        if (plays.Count == 0)
        {
            return 0;
        }

        var byEpisode = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        foreach (var item in _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Episode }, AncestorIds = new[] { library.Id } }))
        {
            if (item.Path is { } path && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                && FollowFiles.ParseStrm(File.ReadAllText(path)) is { } ids)
            {
                byEpisode[ids.ContentId] = item;
            }
        }

        var copied = 0;
        foreach (var play in plays)
        {
            if (!byEpisode.TryGetValue(play.EpisodeId, out var mirror) || _userData.GetUserData(play.User, mirror) is not { } mirrorData
                || !WatchState.ShouldCopy(play.Data, mirrorData))
            {
                continue;
            }

            WatchState.Copy(play.Data, mirrorData);
            _userData.SaveUserData(play.User, mirror, mirrorData, UserDataSaveReason.Import, cancellationToken);
            if (play.Data.PlaybackPositionTicks > 0)
            {
                play.Data.PlaybackPositionTicks = 0;
                _userData.SaveUserData(play.User, play.Item, play.Data, UserDataSaveReason.Import, cancellationToken);
            }

            copied++;
        }

        return copied;
    }
}
