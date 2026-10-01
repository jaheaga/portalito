using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>What the series repair changes on one saved item; null fields stay as they are.</summary>
public sealed record SeriesRepairChange(long? RunTimeTicks, int? ParentIndexNumber)
{
    public bool IsEmpty => RunTimeTicks is null && ParentIndexNumber is null;
}

/// <summary>The decisions of <see cref="SeriesRepairTask"/>, separate from Jellyfin so they can be tested.</summary>
public static class SeriesRepair
{
    /// <summary>
    /// For one saved Portalito movie/episode: its known runtime when it has none (or a different one), and for an
    /// episode with no season, the season -- its parent season folder's number, else the marker in its name
    /// ("Show T2_05": the underscore is read as a separator), else 1.
    /// </summary>
    public static SeriesRepairChange Plan(
        string? externalId,
        long? runTimeTicks,
        int? parentIndexNumber,
        string? name,
        int? parentSeasonNumber,
        RuntimeStore runtimes)
    {
        if (!VodItemId.TryParse(externalId, out var id) || id.Kind is not (VodItemKind.Movie or VodItemKind.Episode))
        {
            return new SeriesRepairChange(null, null);
        }

        var contentId = id.Kind == VodItemKind.Movie ? id.Primary : id.Secondary!;
        long? runtime = runtimes.TryGet(contentId, out var known) && known.Ticks != runTimeTicks ? known.Ticks : null;

        int? season = null;
        if (id.Kind == VodItemKind.Episode && parentIndexNumber is null or 0)
        {
            season = parentSeasonNumber is > 0 ? parentSeasonNumber : SeasonGrouping.SeasonFromName(name?.Replace('_', ' '));
        }

        return new SeriesRepairChange(runtime, season);
    }
}

/// <summary>
/// Fills in, on Portalito items Jellyfin already saved, what it only reads when it first creates an item
/// (ChannelManager copies <c>ParentIndexNumber</c> on creation only) or only saves on a forced update
/// (<c>RunTimeTicks</c>): the runtime (what makes Continue Watching work) and the episode's season (what Next Up and
/// episode ordering need). Runs at startup -- so an upgrade repairs right away -- and weekly.
/// </summary>
public sealed class SeriesRepairTask : IScheduledTask
{
    private readonly IChannelManager _channelManager;
    private readonly ILibraryManager _libraryManager;
    private readonly RuntimeStore _runtimes;
    private readonly ILogger<SeriesRepairTask> _logger;

    public SeriesRepairTask(IChannelManager channelManager, ILibraryManager libraryManager, RuntimeStore runtimes, ILogger<SeriesRepairTask> logger)
    {
        _channelManager = channelManager;
        _libraryManager = libraryManager;
        _runtimes = runtimes;
        _logger = logger;
    }

    public string Name => "Reparar series Portalito";

    public string Key => "PortalitoSeriesRepair";

    public string Description => "Completa la duración y la temporada de las películas y episodios de Portalito para que aparezcan en \"Continuar viendo\".";

    public string Category => "Portalito";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.WeeklyTrigger, DayOfWeek = DayOfWeek.Sunday, TimeOfDayTicks = new TimeSpan(3, 45, 0).Ticks },
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var channels = await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        var channel = channels.Items.FirstOrDefault(c => c.Name == "Portalito VOD");
        if (channel is null)
        {
            _logger.LogInformation("Series repair: the Portalito VOD channel isn't registered yet; nothing to repair");
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ChannelIds = new[] { channel.Id },
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
        });

        int runtimes = 0, seasons = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            var change = SeriesRepair.Plan(item.ExternalId, item.RunTimeTicks, item.ParentIndexNumber, item.Name, (item as Episode)?.Season?.IndexNumber, _runtimes);
            if (change.IsEmpty)
            {
                continue;
            }

            if (change.RunTimeTicks is { } ticks)
            {
                item.RunTimeTicks = ticks;
                runtimes++;
            }

            if (change.ParentIndexNumber is { } season)
            {
                item.ParentIndexNumber = season;
                seasons++;
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (i + 1) / items.Count);
        }

        _logger.LogInformation(
            "Series repair: {Runtimes} runtimes and {Seasons} episode seasons filled in, of {Total} saved Portalito titles ({Known} runtimes known)",
            runtimes,
            seasons,
            items.Count,
            _runtimes.Count);
        progress.Report(100);
    }
}
