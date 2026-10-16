using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.PortalitoTV.Util;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Media;

/// <summary>An audio track as ffprobe reports it.</summary>
/// <param name="Language">The language tag.</param>
/// <param name="Title">The track title.</param>
public sealed record AudioTrack(string? Language, string? Title);

/// <summary>What was measured of an item: duration and audio tracks (null = not known yet).</summary>
public sealed class ProbeEntry
{
    /// <summary>Gets or sets the duration in milliseconds (0 = unknown).</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the audio tracks (null = not probed yet).</summary>
    public List<AudioTrack>? Audio { get; set; }

    /// <summary>Gets or sets when it was last probed.</summary>
    public DateTime ProbedUtc { get; set; }

    /// <summary>Gets or sets the consecutive failures (portal down): retries are spaced out.</summary>
    public int Failures { get; set; }

    /// <summary>Gets the duration.</summary>
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>
/// The ffprobe cache. Key: the .strm's path from the show's folder on for episodes, or "movie:&lt;id&gt;" for movies.
/// </summary>
public sealed class ProbeCache
{
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<ProbeCache> _logger;
    private readonly object _lock = new();
    private Dictionary<string, ProbeEntry>? _entries;
    private bool _dirty;

    /// <summary>Initializes a new instance of the <see cref="ProbeCache"/> class.</summary>
    /// <param name="encoder">Jellyfin's media encoder (ffprobe).</param>
    /// <param name="logger">Logger.</param>
    public ProbeCache(IMediaEncoder encoder, ILogger<ProbeCache> logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.WorkDir, "probe.json");

    private Dictionary<string, ProbeEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries ??= JsonFile.Load<Dictionary<string, ProbeEntry>>(FilePath) ?? new();
            }
        }
    }

    /// <summary>Looks an entry up.</summary>
    /// <param name="key">Cache key.</param>
    /// <returns>The entry, or null if nothing was measured yet.</returns>
    public ProbeEntry? Get(string key)
    {
        var entries = Entries;
        lock (_lock)
        {
            return entries.TryGetValue(key, out var e) ? e : null;
        }
    }

    /// <summary>Stores a duration known another way (Jellyfin) without overwriting what was measured.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="durationMs">The duration in milliseconds.</param>
    /// <param name="audio">The audio tracks, if known.</param>
    public void SetDurationIfMissing(string key, long durationMs, List<AudioTrack>? audio = null)
    {
        var entries = Entries;
        lock (_lock)
        {
            if (!entries.TryGetValue(key, out var e))
            {
                entries[key] = e = new ProbeEntry();
            }

            if (e.DurationMs <= 0 && durationMs > 0)
            {
                e.DurationMs = durationMs;
                _dirty = true;
            }

            if (e.Audio is null && audio is not null)
            {
                e.Audio = audio;
                _dirty = true;
            }
        }
    }

    /// <summary>Whether it is worth probing an item now (retries after failures are spaced out).</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="needAudio">Whether the audio tracks are needed too.</param>
    /// <returns>True if it should be probed.</returns>
    public bool ShouldProbe(string key, bool needAudio)
    {
        var e = Get(key);
        if (e is not null && e.DurationMs > 0 && (!needAudio || e.Audio is not null))
        {
            return false;
        }

        if (e is null || e.Failures == 0)
        {
            return true;
        }

        var wait = TimeSpan.FromMinutes(Math.Min(360, 15 * Math.Pow(2, e.Failures - 1)));
        return DateTime.UtcNow - e.ProbedUtc > wait;
    }

    /// <summary>Runs ffprobe on the URL and stores the result.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="url">The input URL.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entry, or null if the probe failed.</returns>
    public async Task<ProbeEntry?> ProbeAsync(string key, string url, CancellationToken ct)
    {
        string json;
        try
        {
            json = await RunFfprobeAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            json = string.Empty;
        }

        double seconds = 0;
        List<AudioTrack>? audio = null;
        try
        {
            if (json.Length > 0)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
                {
                    double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                }

                if (doc.RootElement.TryGetProperty("streams", out var streams))
                {
                    audio = streams.EnumerateArray()
                        .Where(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "audio")
                        .Select(s => s.TryGetProperty("tags", out var tags)
                            ? new AudioTrack(Tag(tags, "language"), Tag(tags, "title"))
                            : new AudioTrack(null, null))
                        .ToList();
                }
            }
        }
        catch (JsonException)
        {
            // Unreadable ffprobe output counts as a failed probe (below).
        }

        var entries = Entries;
        lock (_lock)
        {
            if (!entries.TryGetValue(key, out var e))
            {
                entries[key] = e = new ProbeEntry();
            }

            e.ProbedUtc = DateTime.UtcNow;
            _dirty = true;
            if (seconds >= 60 && audio is not null)
            {
                e.DurationMs = (long)(seconds * 1000);
                e.Audio = audio;
                e.Failures = 0;
                return e;
            }

            e.Failures++;
            return null;
        }
    }

    /// <summary>Writes the cache to disk if it changed.</summary>
    public void SaveIfDirty()
    {
        Dictionary<string, ProbeEntry> copy;
        lock (_lock)
        {
            if (!_dirty || _entries is null)
            {
                return;
            }

            copy = new Dictionary<string, ProbeEntry>(_entries);
            _dirty = false;
        }

        JsonFile.Save(FilePath, copy);
    }

    private static string? Tag(JsonElement tags, string name) =>
        tags.TryGetProperty(name, out var v) ? v.GetString() : null;

    private async Task<string> RunFfprobeAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var psi = new ProcessStartInfo(_encoder.ProbePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-v", "error", "-rw_timeout", "30000000",
                     "-show_entries", "format=duration:stream=index,codec_type:stream_tags=language,title",
                     "-of", "json", url,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)!;
        try
        {
            var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var err = await stderr.ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                _logger.LogDebug("ffprobe failed: {Error}", Streaming.FfmpegRunner.Redact(err.Trim()));
            }

            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
    }
}
