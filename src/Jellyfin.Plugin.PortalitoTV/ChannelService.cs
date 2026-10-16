using Jellyfin.Plugin.PortalitoTV.Content;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV;

/// <summary>Runs a full pass: read the hearts, probe what is missing and extend the grids.</summary>
public sealed class ChannelService
{
    private readonly ContentCatalog _catalog;
    private readonly PlayoutManager _playouts;
    private readonly ITaskManager _tasks;
    private readonly IConfigurationManager _config;
    private readonly ILogger<ChannelService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ChannelService"/> class.</summary>
    /// <param name="catalog">The content catalog.</param>
    /// <param name="playouts">The schedules.</param>
    /// <param name="tasks">Jellyfin's task manager.</param>
    /// <param name="config">Jellyfin's configuration manager.</param>
    /// <param name="logger">Logger.</param>
    public ChannelService(ContentCatalog catalog, PlayoutManager playouts, ITaskManager tasks,
        IConfigurationManager config, ILogger<ChannelService> logger)
    {
        _config = config;
        _catalog = catalog;
        _playouts = playouts;
        _tasks = tasks;
        _logger = logger;
    }

    /// <summary>Reads the hearts, probes new episodes and extends the schedules.</summary>
    /// <param name="probeBudget">Maximum time to probe pending episodes (zero = don't probe).</param>
    /// <param name="reprogram">Rebuild the schedule from the program on air (after the configuration changed).</param>
    /// <param name="progress">Overall progress, 0-100.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="phase">Called when the stage changes with a text for the user (the page shows it on the progress bar).</param>
    /// <returns>A task that completes when the pass is done.</returns>
    public async Task RunAsync(TimeSpan probeBudget, bool reprogram, IProgress<double>? progress, CancellationToken ct,
        Action<string>? phase = null)
    {
        phase?.Invoke("Leyendo tus ♥ de Portalito…");
        await _catalog.RefreshAsync(ct).ConfigureAwait(false);
        progress?.Report(20);

        if (probeBudget > TimeSpan.Zero && (_catalog.PendingCount > 0 || _catalog.SampleCount > 0))
        {
            phase?.Invoke("Midiendo episodios nuevos…");
            var probed = await _catalog.ProbePendingAsync(probeBudget, ct, (processed, total) =>
            {
                // Probing takes from 20 to 80 % of this stage.
                progress?.Report(20 + (60.0 * processed / Math.Max(1, total)));
                phase?.Invoke($"Midiendo episodios nuevos ({processed} de {total})…");
            }).ConfigureAwait(false);
            _logger.LogInformation("Probed {Probed} new items ({Pending} were pending)", probed, _catalog.PendingCount);
            if (probed > 0)
            {
                await _catalog.RefreshAsync(ct).ConfigureAwait(false);
            }
        }

        progress?.Report(80);
        phase?.Invoke("Armando la programación de los canales…");
        await _playouts.UpdateAsync(reprogram, ct).ConfigureAwait(false);
        progress?.Report(100);
        if (reprogram)
        {
            RefreshGuide();
        }
    }

    /// <summary>Starts Jellyfin's "Refresh Guide" so the guide shows the changes right away.</summary>
    public void RefreshGuide()
    {
        // Jellyfin reuses its copy of the XMLTV if it is less than a day old: it has to be deleted so it is downloaded again.
        var options = (LiveTvOptions)_config.GetConfiguration("livetv");
        foreach (var provider in options.ListingProviders.Where(p => p.Path?.Contains("/PortalitoTV/", StringComparison.Ordinal) == true))
        {
            var cached = Path.Combine(_config.CommonApplicationPaths.CachePath, "xmltv", provider.Id + ".xml");
            try
            {
                File.Delete(cached);
            }
            catch (IOException e)
            {
                _logger.LogWarning("Could not delete the cached guide: {Error}", e.Message);
            }
        }

        var worker = _tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
        if (worker is not null)
        {
            _ = _tasks.Execute(worker, new TaskOptions());
        }
    }
}
