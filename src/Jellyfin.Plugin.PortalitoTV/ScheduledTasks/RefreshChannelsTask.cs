using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.PortalitoTV.ScheduledTasks;

/// <summary>Every 30 min (and at startup): reads the hearts, probes new episodes and extends the grids to 48 h.</summary>
public sealed class RefreshChannelsTask : IScheduledTask
{
    private readonly ChannelService _service;

    /// <summary>Initializes a new instance of the <see cref="RefreshChannelsTask"/> class.</summary>
    /// <param name="service">The channel service.</param>
    public RefreshChannelsTask(ChannelService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Actualizar canales";

    /// <inheritdoc />
    public string Key => "PortalitoTVRefresh";

    /// <inheritdoc />
    public string Description => "Lee las series y películas con ♥, mide lo nuevo y extiende la programación de los canales.";

    /// <inheritdoc />
    public string Category => "Portalito TV";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _service.RunAsync(TimeSpan.FromMinutes(20), reprogram: false, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks },
    };
}
