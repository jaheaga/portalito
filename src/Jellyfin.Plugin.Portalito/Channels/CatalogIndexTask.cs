using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>Which folders the catalog index opens, by the folder's item id (a <see cref="VodItemId"/> string).</summary>
public static class CatalogIndexWalk
{
    /// <summary>
    /// Descubrir, its catalogs, their "Por año" folders and every year folder (which list whole years, so together
    /// they reach nearly the whole catalog), plus "TV en vivo" and its all-channels category. Not "Todo"/genre folders
    /// (newest-200 subsets of the same titles) nor shows/seasons (the show itself is what search needs to find).
    /// </summary>
    public static bool ShouldOpen(string? externalId)
    {
        if (!VodItemId.TryParse(externalId, out var id))
        {
            return false;
        }

        return id.Kind switch
        {
            VodItemKind.Discover or VodItemKind.Catalog or VodItemKind.LiveRoot or VodItemKind.LiveCategory => true,
            VodItemKind.Filter => id.Secondary is { } filter && (filter == "years" || filter.StartsWith('y')),
            _ => false,
        };
    }
}

/// <summary>
/// Makes the whole Portalito catalog findable in Jellyfin's own search box. Jellyfin's search only queries its library
/// database, and channel items only get into it when their folder is listed (measured live 2026-09-24: already-browsed
/// Portalito movies, series and live channels came back from /Search/Hints; unbrowsed ones can't). Jellyfin 10.11 has no
/// channel search hook (ISearchableChannel is gone), so this task lists the folders <see cref="CatalogIndexWalk"/>
/// picks through Jellyfin's own ChannelManager -- the same call its "latest items" refresh uses -- which saves every
/// item those folders return. Found by Jellyfin's type scan, like any plugin task; shown under Scheduled Tasks.
/// </summary>
public sealed class CatalogIndexTask : IScheduledTask
{
    // Between folders: the index makes a few hundred portal calls, and the portal has been measured returning
    // incomplete listings under rapid repeated load (RootShelfCache history, 2026-09-21).
    private static readonly TimeSpan PauseBetweenFolders = TimeSpan.FromMilliseconds(300);

    private readonly IChannelManager _channelManager;
    private readonly ILogger<CatalogIndexTask> _logger;

    public CatalogIndexTask(IChannelManager channelManager, ILogger<CatalogIndexTask> logger)
    {
        _channelManager = channelManager;
        _logger = logger;
    }

    public string Name => "Indexar catálogo Portalito";

    public string Key => "PortalitoCatalogIndex";

    public string Description => "Guarda todo el catálogo de Portalito (películas, series y TV en vivo) para que aparezca en la búsqueda de Jellyfin.";

    public string Category => "Portalito";

    // Weekly, at night: a full walk is a few hundred portal calls and tens of thousands of item upserts, and new
    // titles already get saved whenever someone opens the folder they're in.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.WeeklyTrigger, DayOfWeek = DayOfWeek.Sunday, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks },
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var channels = await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        var channel = channels.Items.FirstOrDefault(c => c.Name == "Portalito VOD");
        if (channel is null)
        {
            _logger.LogWarning("Catalog index: the Portalito VOD channel isn't registered (is the plugin configured?)");
            return;
        }

        var pending = new Queue<Guid>();
        pending.Enqueue(Guid.Empty); // the channel root
        int opened = 0, saved = 0, failed = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentId = pending.Dequeue();
            try
            {
                var result = await _channelManager.GetChannelItemsInternal(
                    new InternalItemsQuery { ChannelIds = new[] { channel.Id }, ParentId = parentId, EnableTotalRecordCount = false },
                    new Progress<double>(),
                    cancellationToken).ConfigureAwait(false);
                opened++;
                saved += result.Items.Count;
                foreach (var item in result.Items)
                {
                    if (item.IsFolder && CatalogIndexWalk.ShouldOpen(item.ExternalId))
                    {
                        pending.Enqueue(item.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Catalog index: listing folder {ParentId} failed; continuing", parentId);
            }

            // The queue only ever grows by what's discovered, so this is an estimate that settles as the walk goes.
            progress.Report(100.0 * opened / (opened + pending.Count));
            await Task.Delay(PauseBetweenFolders, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Catalog index: opened {Opened} folders, saved {Saved} items, {Failed} folders failed", opened, saved, failed);
    }
}
