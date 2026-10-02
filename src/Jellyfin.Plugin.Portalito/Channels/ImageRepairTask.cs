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
            && id.Kind is VodItemKind.Featured or VodItemKind.Row or VodItemKind.Discover or VodItemKind.Catalog or VodItemKind.Filter or VodItemKind.LiveRoot or VodItemKind.LiveCategory;

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

    /// <summary>The folders walked to learn every collage folder's current image (a curated row holds titles, not folders).</summary>
    public static bool ShouldOpen(string? externalId)
        => VodItemId.TryParse(externalId, out var id) && id.Kind switch
        {
            VodItemKind.Featured or VodItemKind.Discover or VodItemKind.Catalog or VodItemKind.LiveRoot => true,
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

    public string Description => "Vuelve a poner las miniaturas de las carpetas de Portalito (Descubrir, categorías, géneros, años y TV en vivo) y quita imágenes rotas.";

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

        var desired = await CurrentCollagesAsync(vodChannel, progress, cancellationToken).ConfigureAwait(false);

        var items = _libraryManager.GetItemList(new InternalItemsQuery { ChannelIds = new[] { channel.Id } });
        int swapped = 0, cleared = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primary = item.GetImageInfo(ImageType.Primary, 0);
            if (item.ExternalId is { } externalId && desired.TryGetValue(externalId, out var collage))
            {
                if (primary?.Path == collage)
                {
                    continue;
                }

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

                item.SetImagePath(ImageType.Primary, collage);
                await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                swapped++;
            }
            else if (primary is not null && ImageRepair.IsBrokenTitlePoster(item.ExternalId, primary.Path))
            {
                await item.DeleteImageAsync(ImageType.Primary, 0).ConfigureAwait(false);
                cleared++;
            }
        }

        _logger.LogInformation("Image repair: {Swapped} folder collages updated, {Cleared} broken title posters cleared, of {Total} saved items", swapped, cleared, items.Count);

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

    /// <summary>Every collage folder's current image, straight from the channel (rendering any that are new).</summary>
    private async Task<Dictionary<string, string>> CurrentCollagesAsync(PortalitoVodChannel vodChannel, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
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
                foreach (var item in result.Items.Where(i => i.Type == ChannelItemType.Folder))
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Image repair: listing folder {FolderId} failed; continuing", folderId);
            }

            progress.Report(80.0 * opened / (opened + pending.Count));
            await Task.Delay(PauseBetweenFolders, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Image repair: listed {Opened} folders ({Failed} failed), {Collages} collage folders", opened, failed, desired.Count);
        return desired;
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
