using System.Globalization;
using Jellyfin.Plugin.PortalitoTV.Content;
using Jellyfin.Plugin.PortalitoTV.Media;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Streaming;

/// <summary>
/// The "inner" ffmpeg: a stretch of an item normalized to 1080p H.264 (VAAPI) + stereo AAC in MPEG-TS, with the chosen audio
/// track and, when asked for, the Spanish subtitle burned in. Every stretch comes out in the same format so the "outer" ffmpeg
/// can chain them with <c>-c copy</c>.
/// </summary>
public sealed class ItemTranscoder
{
    private const string DefaultVaapiDevice = "/dev/dri/renderD128";
    private const string VideoFilterBase =
        "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:-1:-1,setsar=1";

    private readonly IMediaEncoder _encoder;
    private readonly ILibraryManager _library;
    private readonly SubtitleCache _subtitles;
    private readonly ProbeCache _probe;
    private readonly InputResolver _inputs;
    private readonly ILogger<ItemTranscoder> _logger;

    /// <summary>Initializes a new instance of the <see cref="ItemTranscoder"/> class.</summary>
    /// <param name="encoder">Jellyfin's media encoder (ffmpeg).</param>
    /// <param name="library">The library.</param>
    /// <param name="subtitles">Spanish subtitles as local files.</param>
    /// <param name="probe">Probed tracks.</param>
    /// <param name="inputs">Input URL resolver.</param>
    /// <param name="logger">Logger.</param>
    public ItemTranscoder(IMediaEncoder encoder, ILibraryManager library, SubtitleCache subtitles, ProbeCache probe,
        InputResolver inputs, ILogger<ItemTranscoder> logger)
    {
        _encoder = encoder;
        _library = library;
        _subtitles = subtitles;
        _probe = probe;
        _inputs = inputs;
        _logger = logger;
    }

    private static string VaapiDevice
    {
        get
        {
            var configured = Plugin.Instance?.Configuration.VaapiDevice;
            return string.IsNullOrWhiteSpace(configured) ? DefaultVaapiDevice : configured.Trim();
        }
    }

    /// <summary>Streams <paramref name="length"/> of the item from <paramref name="offset"/> (position inside the item).</summary>
    /// <param name="item">The program.</param>
    /// <param name="offset">Where in the item to start.</param>
    /// <param name="length">How much to stream.</param>
    /// <param name="burst">Seconds sent at full speed at the start (to fill the buffer).</param>
    /// <param name="output">Where the MPEG-TS goes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True if ffmpeg finished cleanly and sent something.</returns>
    public async Task<bool> StreamItemAsync(PlayoutItem item, TimeSpan offset, TimeSpan length, double burst, Stream output,
        CancellationToken ct)
    {
        var video = _library.GetItemById(item.ItemId);
        if (video is null)
        {
            _logger.LogWarning("Item {Id} no longer exists in Jellyfin", item.ItemId);
            return false;
        }

        var input = await _inputs.ResolveAsync(video, ct).ConfigureAwait(false);
        if (input is null)
        {
            _logger.LogWarning("{Name}: could not resolve the input URL", video.Name);
            return false;
        }

        // Tracks: what was measured, or measured now (adds ~2 s; normally it was measured before).
        var key = InputResolver.ProbeKey(video);
        var audio = _probe.Get(key)?.Audio
                    ?? (await _probe.ProbeAsync(key, input, ct).ConfigureAwait(false))?.Audio
                    ?? new List<AudioTrack>();
        _probe.SaveIfDirty();

        // Subtitle: only when the choice asks for it (English audio); it is fetched before deciding to know whether it exists.
        var tentative = TrackSelector.Choose(audio, item.Language, hasSpanishSubtitle: true);
        string? srt = tentative.BurnSubtitles ? await _subtitles.GetSpanishSrtAsync(video, ct).ConfigureAwait(false) : null;
        var choice = TrackSelector.Choose(audio, item.Language, srt is not null);

        var args = BuildItemArgs(input, offset, length, burst, choice.AudioOrdinal,
            choice.BurnSubtitles && srt is not null ? _encoder.EscapeSubtitleFilterPath(srt) : null);
        var (exit, bytes) = await FfmpegRunner.RunToStreamAsync(_encoder.EncoderPath, args, output, _logger, video.Name, ct)
            .ConfigureAwait(false);
        return exit == 0 && bytes > 0;
    }

    /// <summary>Black slate with silence, in the same format as the items (for gaps or portal errors).</summary>
    /// <param name="length">How long it lasts.</param>
    /// <param name="output">Where the MPEG-TS goes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes when the slate has been sent.</returns>
    public async Task StreamSlateAsync(TimeSpan length, Stream output, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-init_hw_device", "vaapi=va:" + VaapiDevice, "-filter_hw_device", "va",
            "-readrate", "1",
            "-f", "lavfi", "-i", "color=c=black:s=1920x1080:r=30000/1001",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", Seconds(length),
            "-vf", "format=nv12,hwupload",
        };
        args.AddRange(OutputArgs("0:v", "1:a"));
        await FfmpegRunner.RunToStreamAsync(_encoder.EncoderPath, args, output, _logger, "slate", ct).ConfigureAwait(false);
    }

    private static List<string> BuildItemArgs(string input, TimeSpan offset, TimeSpan length, double burst, int audioOrdinal,
        string? escapedSrt)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-init_hw_device", "vaapi=va:" + VaapiDevice, "-filter_hw_device", "va",
            "-hwaccel", "vaapi", // decodes on the GPU and downloads the frames (falls back to CPU if the codec can't)
            "-readrate", "1", "-readrate_initial_burst", burst.ToString("0.#", CultureInfo.InvariantCulture),
            "-rw_timeout", "30000000",
            "-reconnect", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "5",
        };
        if (offset > TimeSpan.FromSeconds(1))
        {
            args.Add("-ss");
            args.Add(Seconds(offset));
        }

        args.Add("-t");
        args.Add(Seconds(length));
        args.Add("-i");
        args.Add(input);

        // The subtitles filter uses the frame time: after -ss the timestamps are relative to the requested point, so they are
        // shifted by the offset for the subtitle and shifted back afterwards. Don't use STARTPTS: when entering mid-GOP
        // (HEVC) the first frame comes out ~1 s after the audio, and STARTPTS would move it to 0 and leave the audio ahead
        // (measured: 1.2 s on an HEVC episode).
        var vf = "[0:v:0]" + VideoFilterBase;
        if (escapedSrt is not null)
        {
            var o = offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            vf += $",setpts=PTS+{o}/TB,subtitles=filename='{escapedSrt}',setpts=PTS-{o}/TB";
        }

        vf += ",format=nv12,hwupload[v]";
        args.Add("-filter_complex");
        args.Add(vf);
        args.AddRange(OutputArgs("[v]", "0:a:" + audioOrdinal.ToString(CultureInfo.InvariantCulture) + "?"));
        return args;
    }

    /// <summary>The common output encoding (the same for items and the slate).</summary>
    private static List<string> OutputArgs(string videoMap, string audioMap) => new()
    {
        "-map", videoMap,
        "-map", audioMap,
        "-c:v", "h264_vaapi", "-b:v", "6M", "-maxrate", "8M", "-bufsize", "12M", "-g", "60",
        "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-ar", "48000", "-af", "aresample=async=1:first_pts=0",
        "-muxdelay", "0", "-muxpreload", "0",
        "-f", "mpegts", "pipe:1",
    };

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
