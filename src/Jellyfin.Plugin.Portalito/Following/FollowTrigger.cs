using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Runs the "Siguiendo" sync as soon as someone stops a Portalito channel episode, instead of up to 30 minutes later:
/// that's when a show becomes followed and when there's new progress to carry over, so Next Up shows the next
/// episode by the time they're back on the home screen.
/// </summary>
public sealed class FollowTrigger : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly ITaskManager _tasks;

    public FollowTrigger(ISessionManager sessions, ITaskManager tasks)
    {
        _sessions = sessions;
        _tasks = tasks;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <summary>Stops within this long of a queued sync don't queue another: skipping through episodes would chain full runs.</summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromMinutes(1);

    private long _lastQueuedTicks;

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (Plugin.Instance?.Configuration is { FollowLibraryEnabled: true }
            && e.Item is { SourceType: SourceType.Channel } item
            && VodItemId.TryParse(item.ExternalId, out var id)
            && id.Kind == VodItemKind.Episode
            && ShouldQueue(ref _lastQueuedTicks, DateTime.UtcNow.Ticks))
        {
            _tasks.QueueScheduledTask<FollowSyncTask>();
        }
    }

    /// <summary>Whether a sync may be queued now; records the time when it may. Thread-safe.</summary>
    internal static bool ShouldQueue(ref long lastQueuedTicks, long nowTicks)
    {
        var last = Interlocked.Read(ref lastQueuedTicks);
        return nowTicks - last >= Debounce.Ticks && Interlocked.CompareExchange(ref lastQueuedTicks, nowTicks, last) == last;
    }
}
