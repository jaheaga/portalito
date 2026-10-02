using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>A title's known runtime and when the plugin learned it.</summary>
public sealed record KnownRuntime(long Ticks, DateTime LearnedUtc);

/// <summary>
/// Remembers each title's runtime (by portal contentId) across restarts. Jellyfin only keeps a playback position when
/// the item has a runtime -- otherwise <c>UserDataManager.UpdatePlayState</c> "assumes it was fully played", marks it
/// Played and drops the position (measured 2026-10-01: every Portalito item had no runtime, so nothing ever reached
/// Continue Watching). The portal rarely sends a duration (empty on every episode measured), so the main source is the
/// ffprobe that runs when a title starts playing; this store carries that runtime into later listings and repairs.
/// </summary>
public sealed class RuntimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(5);

    private readonly string? _path;
    private readonly ConcurrentDictionary<string, KnownRuntime> _runtimes;
    private readonly object _saveGate = new();
    private int _savePending;

    /// <param name="path">The JSON file to load and save; null keeps the store in memory only (tests).</param>
    public RuntimeStore(string? path)
    {
        _path = path;
        _runtimes = new ConcurrentDictionary<string, KnownRuntime>(Load(path), StringComparer.Ordinal);
    }

    public int Count => _runtimes.Count;

    public bool TryGet(string contentId, out KnownRuntime runtime) => _runtimes.TryGetValue(contentId, out runtime!);

    /// <summary>Records a runtime; returns whether it was new or different (and so saved).</summary>
    public bool Set(string contentId, long ticks, DateTime nowUtc)
    {
        if (ticks <= 0 || (_runtimes.TryGetValue(contentId, out var known) && known.Ticks == ticks))
        {
            return false;
        }

        _runtimes[contentId] = new KnownRuntime(ticks, nowUtc);
        ScheduleSave();
        return true;
    }

    /// <summary>Writes pending changes now (tests).</summary>
    public void Flush()
    {
        Interlocked.Exchange(ref _savePending, 0);
        Save();
    }

    // One write per burst: a listing whose titles carry a portal duration sets hundreds at once, and rewriting the whole
    // file for each was quadratic and blocked the listing.
    private void ScheduleSave()
    {
        if (_path is null || Interlocked.Exchange(ref _savePending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(SaveDelay).ContinueWith(_ => Flush(), TaskScheduler.Default);
    }

    /// <summary>
    /// A portal <c>duration</c> as ticks: whole seconds ("2700"), or "HH:MM:SS"/"MM:SS" -- the shapes the portal's own
    /// app accepts. Null for empty or unparseable values.
    /// </summary>
    public static long? ParseDuration(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > 0 ? TimeSpan.FromSeconds(seconds).Ticks : null;
        }

        var parts = text.Split(':');
        if (parts.Length is 2 or 3 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var span = parts.Length == 3 ? new TimeSpan(n[0], n[1], n[2]) : new TimeSpan(0, n[0], n[1]);
            return span > TimeSpan.Zero ? span.Ticks : null;
        }

        return null;
    }

    private static Dictionary<string, KnownRuntime> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, KnownRuntime>>(File.ReadAllText(path), JsonOptions)
                    ?? new Dictionary<string, KnownRuntime>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Moved aside, never overwritten by the near-empty store the next save would write: a damaged file is
            // recoverable by hand, and otherwise only costs re-learning runtimes as titles are played again.
            try
            {
                File.Move(path!, path + ".bad", overwrite: true);
            }
            catch (Exception moveFailed) when (moveFailed is IOException or UnauthorizedAccessException)
            {
                // Nothing more to do.
            }
        }

        return new Dictionary<string, KnownRuntime>();
    }

    // Small file (one entry per title ever played), written whole on each change; atomic via a temp file + move.
    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        lock (_saveGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_runtimes, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not fatal: the runtime still applies to this run; it's re-learned on the next play after a restart.
            }
        }
    }
}
