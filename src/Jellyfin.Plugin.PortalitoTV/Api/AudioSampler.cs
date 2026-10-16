using System.Globalization;
using Jellyfin.Plugin.PortalitoTV.Content;
using Jellyfin.Plugin.PortalitoTV.Media;
using Jellyfin.Plugin.PortalitoTV.Streaming;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Api;

/// <summary>
/// Audio samples for "Escuchar". A fixed point often lands on a scene without dialogue, so 60 s are read and the 15 s stretch
/// with the most speech is returned: where the requested track differs from another track (another dub or language) there is
/// dialogue; music and effects are the same on every track and cancel out.
/// </summary>
public sealed class AudioSampler
{
    private const int Rate = 8000;
    private const int ReadSeconds = 60;
    private const int SampleSeconds = 15;

    private readonly IMediaEncoder _encoder;
    private readonly InputResolver _inputs;
    private readonly ProbeCache _probe;
    private readonly ILogger<AudioSampler> _logger;

    /// <summary>Initializes a new instance of the <see cref="AudioSampler"/> class.</summary>
    /// <param name="encoder">Jellyfin's media encoder (ffmpeg).</param>
    /// <param name="inputs">Input URL resolver.</param>
    /// <param name="probe">Probed tracks.</param>
    /// <param name="logger">Logger.</param>
    public AudioSampler(IMediaEncoder encoder, InputResolver inputs, ProbeCache probe, ILogger<AudioSampler> logger)
    {
        _encoder = encoder;
        _inputs = inputs;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>The second where the 15 s stretch with the most speech starts (energy of L - R; without a reference, energy of L).</summary>
    /// <param name="pcm">Samples (8 kHz, 16-bit stereo, interleaved L/R) read from the item.</param>
    /// <param name="hasReference">Whether R holds a reference track.</param>
    /// <returns>The second where the stretch starts.</returns>
    public static int BestWindow(byte[] pcm, bool hasReference)
    {
        int frames = pcm.Length / 4;
        int seconds = frames / Rate;
        if (seconds <= SampleSeconds)
        {
            return 0;
        }

        var energy = new double[seconds];
        for (int s = 0; s < seconds; s++)
        {
            double sum = 0;
            for (int f = s * Rate; f < (s + 1) * Rate; f++)
            {
                double l = BitConverter.ToInt16(pcm, f * 4);
                double r = BitConverter.ToInt16(pcm, (f * 4) + 2);
                double v = hasReference ? l - r : l;
                sum += v * v;
            }

            energy[s] = sum;
        }

        int best = 0;
        double window = energy.Take(SampleSeconds).Sum(), bestSum = window;
        for (int s = 1; s + SampleSeconds <= seconds; s++)
        {
            window += energy[s + SampleSeconds - 1] - energy[s - 1];
            if (window > bestSum)
            {
                bestSum = window;
                best = s;
            }
        }

        return best;
    }

    /// <summary>Writes the sample of an audio track to a stream.</summary>
    /// <param name="item">The episode or movie.</param>
    /// <param name="track">Index among the audio tracks.</param>
    /// <param name="output">Where the MP3 goes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>False if it couldn't be done (no URL or the portal failed).</returns>
    public async Task<bool> WriteSampleAsync(BaseItem item, int track, Stream output, CancellationToken ct)
    {
        var url = await _inputs.ResolveAsync(item, ct).ConfigureAwait(false);
        if (url is null)
        {
            return false;
        }

        var entry = _probe.Get(InputResolver.ProbeKey(item));
        var audio = entry?.Audio ?? new List<AudioTrack>();
        var duration = entry?.Duration ?? TimeSpan.Zero;
        var at = duration > TimeSpan.FromMinutes(6) ? duration.TotalSeconds / 3 : 120;
        int? other = PickReference(audio, track);

        var tmpDir = Path.Combine(Plugin.Instance!.WorkDir, "tmp");
        Directory.CreateDirectory(tmpDir);
        var wav = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // 1) A single read from the portal: the track in good quality to a .wav and, through stdout, the track and the
            //    reference one at 8 kHz mono interleaved (L = requested, R = reference) to look for speech.
            var reference = other ?? track;
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-rw_timeout", "30000000",
                "-ss", at.ToString("0", CultureInfo.InvariantCulture), "-t", ReadSeconds.ToString(CultureInfo.InvariantCulture),
                "-i", url,
                "-filter_complex",
                $"[0:a:{track}]asplit=2[hq][t];[t]aresample={Rate},aformat=channel_layouts=mono[l];" +
                $"[0:a:{reference}]aresample={Rate},aformat=channel_layouts=mono[r];[l][r]amerge=inputs=2[lr]",
                "-map", "[hq]", "-ac", "2", "-ar", "44100", "-y", wav,
                "-map", "[lr]", "-f", "s16le", "-ac", "2", "-ar", Rate.ToString(CultureInfo.InvariantCulture), "pipe:1",
            };
            using var pcm = new MemoryStream();
            var (exit, bytes) = await FfmpegRunner.RunToStreamAsync(_encoder.EncoderPath, args, pcm, _logger, "sample " + item.Name, ct)
                .ConfigureAwait(false);
            if (exit != 0 || bytes == 0 || !File.Exists(wav))
            {
                return false;
            }

            var from = BestWindow(pcm.ToArray(), other is not null);

            // 2) That stretch, from the local .wav, to MP3.
            var mp3 = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-ss", from.ToString(CultureInfo.InvariantCulture), "-t", SampleSeconds.ToString(CultureInfo.InvariantCulture),
                "-i", wav, "-c:a", "libmp3lame", "-b:a", "128k", "-f", "mp3", "pipe:1",
            };
            await FfmpegRunner.RunToStreamAsync(_encoder.EncoderPath, mp3, output, _logger, "sample mp3", ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            File.Delete(wav);
        }
    }

    /// <summary>Reference track: one in another language if there is one; else any other; if there is only one, none.</summary>
    private static int? PickReference(IReadOnlyList<AudioTrack> audio, int track)
    {
        if (audio.Count < 2 || track >= audio.Count)
        {
            return null;
        }

        string Lang(int i) => (audio[i].Language ?? string.Empty).ToLowerInvariant();
        var otherLanguage = Enumerable.Range(0, audio.Count).FirstOrDefault(i => i != track && Lang(i) != Lang(track), -1);
        return otherLanguage >= 0 ? otherLanguage : Enumerable.Range(0, audio.Count).First(i => i != track);
    }
}
