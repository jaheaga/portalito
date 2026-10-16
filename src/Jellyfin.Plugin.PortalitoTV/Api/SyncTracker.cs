namespace Jellyfin.Plugin.PortalitoTV.Api;

/// <summary>State of the last sync with Portalito ("Sincronizar con Portalito" on the configuration page).</summary>
/// <param name="RunId">Sync number (goes up with each one; 0 = none since Jellyfin started).</param>
/// <param name="Running">In progress.</param>
/// <param name="Percent">Overall progress, 0-100.</param>
/// <param name="Phase">What it is doing now, to show the user.</param>
/// <param name="Error">Message if it failed (null if it finished well or is still running).</param>
/// <param name="StartedUtc">When it started.</param>
/// <param name="FinishedUtc">When it finished.</param>
/// <param name="Pending">Episodes left unprobed when it finished (they are probed in the background).</param>
public sealed record SyncStatus(int RunId, bool Running, double Percent, string Phase, string? Error, DateTime? StartedUtc,
    DateTime? FinishedUtc, int Pending);

/// <summary>Shared state of the sync: one at a time; the page polls it while it runs.</summary>
public static class SyncTracker
{
    private static readonly object Gate = new();
    private static SyncStatus _status = new(0, false, 0, string.Empty, null, null, null, 0);

    /// <summary>The current state.</summary>
    /// <returns>The state.</returns>
    public static SyncStatus Snapshot()
    {
        lock (Gate)
        {
            return _status;
        }
    }

    /// <summary>Marks the start. Returns false (and the current state) if one is already running.</summary>
    /// <param name="phase">The first phase text.</param>
    /// <param name="status">The state after the call.</param>
    /// <returns>True if a new sync was started.</returns>
    public static bool TryStart(string phase, out SyncStatus status)
    {
        lock (Gate)
        {
            if (_status.Running)
            {
                status = _status;
                return false;
            }

            _status = new SyncStatus(_status.RunId + 1, true, 0, phase, null, DateTime.UtcNow, null, 0);
            status = _status;
            return true;
        }
    }

    /// <summary>Updates progress and phase (progress never goes down nor past 99 until it finishes).</summary>
    /// <param name="percent">Overall progress, 0-100.</param>
    /// <param name="phase">The phase text (null keeps the current one).</param>
    public static void Update(double percent, string? phase = null)
    {
        lock (Gate)
        {
            if (!_status.Running)
            {
                return;
            }

            _status = _status with
            {
                Percent = Math.Max(_status.Percent, Math.Min(99, Math.Round(percent, 1))),
                Phase = phase ?? _status.Phase,
            };
        }
    }

    /// <summary>Marks the end.</summary>
    /// <param name="error">The error message, or null if it finished well.</param>
    /// <param name="pending">Episodes left unprobed.</param>
    public static void Finish(string? error, int pending)
    {
        lock (Gate)
        {
            _status = _status with
            {
                Running = false,
                Percent = error is null ? 100 : _status.Percent,
                Phase = error is null ? "Listo" : "Error",
                Error = error,
                FinishedUtc = DateTime.UtcNow,
                Pending = pending,
            };
        }
    }
}
