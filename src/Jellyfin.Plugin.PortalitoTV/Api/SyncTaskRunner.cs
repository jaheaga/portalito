using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.PortalitoTV.Api;

/// <summary>
/// Stage 1 of "Sincronizar con Portalito": runs Jellyfin's "Sincronizar Portalito · Siguiendo" task and waits for it to
/// finish, reporting progress. It doesn't know Jellyfin (it takes functions) so it can be tested with short timings.
/// </summary>
public static class SyncTaskRunner
{
    /// <summary>Percent where waiting for a task that was already running ends and the one we start begins.</summary>
    public const double WaitEnd = 20;

    /// <summary>Percent where the task we start ends (stage 2 follows).</summary>
    public const double RunEnd = 55;

    /// <summary>
    /// If the task is already running (it runs by itself every ~31 min) another can't be started: it waits for it to finish and
    /// then starts a new one, because the running one may have started before the user hearted the new titles. If it
    /// starts by itself right as it is requested (<see cref="InvalidOperationException"/>), that one is awaited.
    /// </summary>
    /// <param name="state">Current state of the task.</param>
    /// <param name="progress">Progress of the task (0-100) or null.</param>
    /// <param name="execute">Requests the task (it only queues it; may throw <see cref="InvalidOperationException"/> if it is already running).</param>
    /// <param name="report">Overall progress (0-100) and the phase text.</param>
    /// <param name="waitingPhase">Text while waiting for the task that was already running.</param>
    /// <param name="runningPhase">Text while the task we started runs.</param>
    /// <param name="timing">Timeouts.</param>
    /// <returns>A task that completes when the sync task has finished (or the timeouts ran out).</returns>
    public static async Task RunAsync(
        Func<TaskState> state,
        Func<double?> progress,
        Func<Task> execute,
        Action<double, string> report,
        string waitingPhase,
        string runningPhase,
        Timing timing)
    {
        if (state() != TaskState.Idle)
        {
            await WaitUntilIdleAsync(state, progress, report, 1, WaitEnd, waitingPhase, timing).ConfigureAwait(false);
        }

        report(WaitEnd, runningPhase);
        try
        {
            await execute().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // It started by itself right now: that one will do, it is awaited below.
        }

        // Execute only queues: wait for it to start and then for it to finish.
        var t0 = DateTime.UtcNow;
        while (state() == TaskState.Idle && DateTime.UtcNow - t0 < timing.StartWait)
        {
            await Task.Delay(timing.StartPoll).ConfigureAwait(false);
        }

        await WaitUntilIdleAsync(state, progress, report, WaitEnd, RunEnd, runningPhase, timing).ConfigureAwait(false);
    }

    /// <summary>Waits for the task to finish (up to <see cref="Timing.MaxWait"/>), reflecting its progress between <paramref name="from"/> and <paramref name="to"/> %.</summary>
    private static async Task WaitUntilIdleAsync(Func<TaskState> state, Func<double?> progress, Action<double, string> report,
        double from, double to, string phase, Timing timing)
    {
        var t0 = DateTime.UtcNow;
        while (state() != TaskState.Idle && DateTime.UtcNow - t0 < timing.MaxWait)
        {
            report(from + ((to - from) * Math.Clamp(progress() ?? 0, 0, 100) / 100), phase);
            await Task.Delay(timing.Poll).ConfigureAwait(false);
        }
    }

    /// <summary>Timeouts.</summary>
    /// <param name="Poll">How often the task is checked while it runs.</param>
    /// <param name="StartPoll">How often it is checked whether the task has started after requesting it.</param>
    /// <param name="StartWait">How long to wait for it to start (Execute only queues it).</param>
    /// <param name="MaxWait">The most to wait for it to finish (each wait).</param>
    public sealed record Timing(TimeSpan Poll, TimeSpan StartPoll, TimeSpan StartWait, TimeSpan MaxWait)
    {
        /// <summary>The timings used in production.</summary>
        public static readonly Timing Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));
    }
}
