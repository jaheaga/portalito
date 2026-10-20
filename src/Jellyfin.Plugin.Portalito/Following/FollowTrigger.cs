using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Runs the "Siguiendo" sync as soon as someone stops a Portalito episode -- in the channel or in the library --
/// instead of up to 30 minutes later: that's when a show becomes followed, when there's new progress to carry over, and
/// when a new episode becomes Next Up and should be made ready, so it's all done by the time they're back on the home screen.
/// </summary>
public sealed class FollowTrigger : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly ITaskManager _tasks;
    private readonly IApplicationPaths _paths;

    public FollowTrigger(ISessionManager sessions, ITaskManager tasks, IApplicationPaths paths)
    {
        _sessions = sessions;
        _tasks = tasks;
        _paths = paths;
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
        if (Plugin.Instance?.Configuration is { FollowLibraryEnabled: true } config
            && e.Item is { } item
            && IsPortalitoEpisode(item.SourceType == SourceType.Channel, item.ExternalId, item.Path, FollowLibrary.Root(config, _paths.DataPath))
            && ShouldQueue(ref _lastQueuedTicks, DateTime.UtcNow.Ticks))
        {
            _tasks.QueueScheduledTask<FollowSyncTask>();
        }
    }

    /// <summary>A channel episode, or an episode of the "Siguiendo" library at <paramref name="root"/>.</summary>
    internal static bool IsPortalitoEpisode(bool fromChannel, string? externalId, string? path, string root)
        => fromChannel
            ? VodItemId.TryParse(externalId, out var id) && id.Kind == VodItemKind.Episode
            : FollowLibrary.Contains(root, path);

    /// <summary>Whether a sync may be queued now; records the time when it may. Thread-safe.</summary>
    internal static bool ShouldQueue(ref long lastQueuedTicks, long nowTicks)
    {
        var last = Interlocked.Read(ref lastQueuedTicks);
        return nowTicks - last >= Debounce.Ticks && Interlocked.CompareExchange(ref lastQueuedTicks, nowTicks, last) == last;
    }
}
