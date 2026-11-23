using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Collage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>Which saved Portalito items the image repair touches.</summary>
public static class ImageRepair
{
    /// <summary>The folders the plugin draws a collage for: the roots, catalogs, genre/year filters, curated rows, and live categories.</summary>
    public static bool IsCollageFolder(string? externalId)
        => VodItemId.TryParse(externalId, out var id)
            && id.Kind is VodItemKind.Featured or VodItemKind.Row or VodItemKind.Discover or VodItemKind.Catalog or VodItemKind.Filter
                or VodItemKind.LiveRoot or VodItemKind.LiveCategory or VodItemKind.XtreamLiveRoot or VodItemKind.XtreamLiveCategory;

    /// <summary>
    /// A title still pointing straight at a portal CDN instead of through <c>/Portalito/img</c>: those predate the proxy
    /// and Jellyfin can't fetch them. Cleared, so the next listing of its folder sets the proxied one. A TMDB poster (the
    /// fallback for titles the portal sends without one) is fine as it is: treating it as broken deleted every one of
    /// them weekly.
    /// </summary>
    public static bool IsBrokenTitlePoster(string? externalId, string? primaryImagePath)
        => !string.IsNullOrEmpty(primaryImagePath)
            && VodItemId.TryParse(externalId, out _)
            && !IsCollageFolder(externalId)
            && primaryImagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && !primaryImagePath.Contains("/Portalito/", StringComparison.Ordinal)
            && !primaryImagePath.StartsWith(Metadata.TmdbClient.PosterBase, StringComparison.OrdinalIgnoreCase);

    /// <summary>A live channel whose logo the repair re-applies (an Xtream channel's backfilled logo, or a portal live channel's).</summary>
    public static bool IsLiveChannel(string? externalId)
        => VodItemId.TryParse(externalId, out var id) && id.Kind is VodItemKind.XtreamLiveChannel or VodItemKind.LiveChannel;

    /// <summary>
    /// The folders walked to learn every collage folder's and live channel's current image. Besides the collage-folder
    /// roots it descends into the live-category folders (portal and Xtream) to reach the channels themselves, whose logos
    /// are backfilled over time and so drift from the image Jellyfin saved on first browse.
    /// </summary>
    public static bool ShouldOpen(string? externalId)
        => VodItemId.TryParse(externalId, out var id) && id.Kind switch
        {
            VodItemKind.Featured or VodItemKind.Discover or VodItemKind.Catalog
                or VodItemKind.LiveRoot or VodItemKind.LiveCategory
                or VodItemKind.XtreamLiveRoot or VodItemKind.XtreamLiveCategory => true,
            VodItemKind.Filter => id.Secondary == "years",
            _ => false,
        };
}

/// <summary>
/// Puts the current collage on every Portalito folder Jellyfin already saved. Jellyfin's ChannelManager only applies a
/// listed item's image when the saved item has none, so a folder browsed before its thumbnail changed keeps the old
/// one forever -- or, worse, the single child poster Jellyfin's own folder image provider put there (see
/// <see cref="CollageService"/>). Weekly too: a folder's collage follows its newest titles.
/// <para>
/// It swaps each image in one step rather than clearing and re-listing: a folder left without an image, even for the
/// seconds a re-list takes, gets a child's poster from that provider on any refresh that lands in between, and a
/// re-list then leaves it be (measured 2026-09-24: "Anime" reverted that way during a clear-then-re-list repair).
/// </para>
/// </summary>
public sealed class ImageRepairTask : IScheduledTask
{
    private static readonly TimeSpan PauseBetweenFolders = TimeSpan.FromMilliseconds(300);

    private readonly IChannelManager _channelManager;
    private readonly IEnumerable<IChannel> _channels;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _paths;
    private readonly CollageService _collages;
    private readonly ILogger<ImageRepairTask> _logger;

    public ImageRepairTask(IChannelManager channelManager, IEnumerable<IChannel> channels, ILibraryManager libraryManager, IApplicationPaths paths, CollageService collages, ILogger<ImageRepairTask> logger)
    {
        _channelManager = channelManager;
        _channels = channels;
        _libraryManager = libraryManager;
        _paths = paths;
        _collages = collages;
        _logger = logger;
    }

    public string Name => "Reparar imágenes Portalito";

    public string Key => "PortalitoImageRepair";

    public string Description => "Vuelve a poner las miniaturas de las carpetas de Portalito (Descubrir, categorías, géneros, años y TV en vivo), los logos de los canales de TV IPTV y quita imágenes rotas.";

    public string Category => "Portalito";

    // Sunday 03:30, just before the catalog index (04:00).
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.WeeklyTrigger, DayOfWeek = DayOfWeek.Sunday, TimeOfDayTicks = new TimeSpan(3, 30, 0).Ticks },
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var channels = await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        var channel = channels.Items.FirstOrDefault(c => c.Name == "Portalito VOD");
        var vodChannel = _channels.OfType<PortalitoVodChannel>().FirstOrDefault();
        if (channel is null || vodChannel is null)
        {
            _logger.LogWarning("Image repair: the Portalito VOD channel isn't registered (is the plugin configured?)");
            return;
        }

        var (desired, names) = await CurrentImagesAsync(vodChannel, progress, cancellationToken).ConfigureAwait(false);

        var items = _libraryManager.GetItemList(new InternalItemsQuery { ChannelIds = new[] { channel.Id } });
        int swapped = 0, cleared = 0, renamed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primary = item.GetImageInfo(ImageType.Primary, 0);
            var externalId = item.ExternalId;
            var update = ItemUpdateType.None;

            if (externalId is not null && desired.TryGetValue(externalId, out var image) && primary?.Path != image)
            {
                if (primary is not null)
                {
                    // A local copy in the item's metadata folder (a downloaded image, or a child poster Jellyfin's
                    // folder provider made) goes too, or Jellyfin finds it on the next refresh and puts it back. A
                    // collage file stays: other folders may share it, and unused ones are pruned below.
                    if (primary.IsLocalFile && !IsCollageFile(primary.Path) && File.Exists(primary.Path))
                    {
                        TryDelete(primary.Path);
                    }

                    item.RemoveImage(primary);
                }

                // The image is a collage file path (folders, name tiles) or a signed /Portalito/img URL (channel logos);
                // Jellyfin re-downloads a URL on next access. This is what forces a saved channel item off a stale logo.
                item.SetImagePath(ImageType.Primary, image);
                update |= ItemUpdateType.ImageUpdate;
                swapped++;
            }

            // Channel display names are cleaned over time (see ChannelDisplayName) and, like images, Jellyfin copies
            // a saved item's name only on first create, so re-apply the current one when it has drifted.
            if (externalId is not null && names.TryGetValue(externalId, out var name) && item.Name != name)
            {
                item.Name = name;
                update |= ItemUpdateType.MetadataEdit;
                renamed++;
            }

            if (update != ItemUpdateType.None)
            {
                await item.UpdateToRepositoryAsync(update, cancellationToken).ConfigureAwait(false);
            }
            else if (primary is not null && ImageRepair.IsBrokenTitlePoster(externalId, primary.Path))
            {
                await item.DeleteImageAsync(ImageType.Primary, 0).ConfigureAwait(false);
                cleared++;
            }
        }

        _logger.LogInformation("Image repair: {Swapped} folder/channel images updated, {Renamed} channels renamed, {Cleared} broken title posters cleared, of {Total} saved items", swapped, renamed, cleared, items.Count);

        // ChannelManager answers a listing it cached in the last 3 hours without touching the saved items at all
        // (GetChannelItemsInternal: "null if came from cache"), so a cleared title poster would only come back after
        // that. Its cache is one JSON file per listing; dropping ours only costs one fresh portal call per folder.
        var listingCache = Path.Combine(_paths.CachePath, "channels", channel.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(listingCache))
        {
            foreach (var file in Directory.EnumerateFiles(listingCache, "*", SearchOption.AllDirectories))
            {
                TryDelete(file);
            }
        }

        // A day's grace: a listing may have just rendered a file whose folder isn't saved yet.
        var inUse = _libraryManager.GetItemList(new InternalItemsQuery { ChannelIds = new[] { channel.Id } })
            .Select(i => i.GetImageInfo(ImageType.Primary, 0)?.Path)
            .OfType<string>()
            .Where(IsCollageFile)
            .ToHashSet(StringComparer.Ordinal);
        var pruned = _collages.Prune(inUse, TimeSpan.FromDays(1));
        _logger.LogInformation("Image repair: {InUse} collages in use, {Pruned} unused ones deleted", inUse.Count, pruned);
        progress.Report(100);
    }

    /// <summary>
    /// Every collage folder's and live channel's current image, straight from the channel (resolving/rendering any
    /// that are new), plus each live channel's current cleaned display name.
    /// </summary>
    private async Task<(Dictionary<string, string> Images, Dictionary<string, string> Names)> CurrentImagesAsync(PortalitoVodChannel vodChannel, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<string?>();
        pending.Enqueue(null); // the channel root
        int opened = 0, failed = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderId = pending.Dequeue();
            try
            {
                var result = await vodChannel.GetChannelItems(new InternalChannelItemQuery { FolderId = folderId! }, cancellationToken).ConfigureAwait(false);
                opened++;
                foreach (var item in result.Items)
                {
                    if (item.Type == ChannelItemType.Folder)
                    {
                        if (ImageRepair.IsCollageFolder(item.Id) && !string.IsNullOrEmpty(item.ImageUrl))
                        {
                            desired[item.Id] = item.ImageUrl;
                        }

                        if (ImageRepair.ShouldOpen(item.Id))
                        {
                            pending.Enqueue(item.Id);
                        }
                    }
                    else if (ImageRepair.IsLiveChannel(item.Id))
                    {
                        // A live channel item: its logo is the backfilled community/TMDB image (or a name tile), and
                        // its name is the cleaned display name -- both drift from what Jellyfin saved on first browse.
                        if (!string.IsNullOrEmpty(item.ImageUrl))
                        {
                            desired[item.Id] = item.ImageUrl;
                        }

                        if (!string.IsNullOrEmpty(item.Name))
                        {
                            names[item.Id] = item.Name;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Image repair: listing folder {FolderId} failed; continuing", folderId);
            }

            progress.Report(80.0 * opened / (opened + pending.Count));
            await Task.Delay(PauseBetweenFolders, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Image repair: listed {Opened} folders ({Failed} failed), {Images} folder/channel images, {Names} channel names", opened, failed, desired.Count, names.Count);
        return (desired, names);
    }

    // One file Jellyfin has open (or that vanished meanwhile) must not abort the repair before the prune below:
    // a stale file here only means that folder refreshes on its own schedule instead.
    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Image repair: couldn't delete {Path} ({Reason}); continuing", path, ex.Message);
        }
    }

    private bool IsCollageFile(string path)
        => path.StartsWith(_collages.Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
