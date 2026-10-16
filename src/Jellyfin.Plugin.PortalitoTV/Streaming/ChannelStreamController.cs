using System.Text;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using Jellyfin.Plugin.PortalitoTV.Util;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Streaming;

/// <summary>
/// Streams a channel the way ErsatzTV Legacy's TS mode does: live.ts is an "outer" ffmpeg that chains, with
/// <c>-c copy</c>, an ffconcat list whose entries are all item.ts; each request to item.ts streams what the grid has from the
/// session's cursor to the end of that program.
/// </summary>
[ApiController]
[Route("PortalitoTV/channel/{number}")]
public sealed class ChannelStreamController : ControllerBase
{
    /// <summary>If the portal fails, a slate for this long and the same program is retried later.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>If the session fell behind real time by more than this, it jumps to "now".</summary>
    private static readonly TimeSpan MaxLag = TimeSpan.FromSeconds(20);

    /// <summary>How far ahead of real time the stream aims to run (a buffer on Jellyfin's side).</summary>
    private static readonly TimeSpan MaxLead = TimeSpan.FromSeconds(15);

    private readonly StreamToken _token;
    private readonly SessionRegistry _sessions;
    private readonly IPlayoutSource _schedule;
    private readonly ItemTranscoder _transcoder;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<ChannelStreamController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ChannelStreamController"/> class.</summary>
    /// <param name="token">The stream key.</param>
    /// <param name="sessions">Active sessions.</param>
    /// <param name="schedule">The channels' schedule.</param>
    /// <param name="transcoder">The inner ffmpeg.</param>
    /// <param name="encoder">Jellyfin's media encoder (ffmpeg).</param>
    /// <param name="logger">Logger.</param>
    public ChannelStreamController(StreamToken token, SessionRegistry sessions, IPlayoutSource schedule, ItemTranscoder transcoder,
        IMediaEncoder encoder, ILogger<ChannelStreamController> logger)
    {
        _token = token;
        _sessions = sessions;
        _schedule = schedule;
        _transcoder = transcoder;
        _encoder = encoder;
        _logger = logger;
    }

    /// <summary>The URL of a channel's continuous stream.</summary>
    /// <param name="number">Channel number.</param>
    /// <param name="token">The stream key.</param>
    /// <returns>The URL (it points at this Jellyfin, see <see cref="ServerUrl"/>).</returns>
    public static string LiveUrl(string number, string token) =>
        $"{ServerUrl.Current}/PortalitoTV/channel/{number}/live.ts?k={token}";

    /// <summary>The continuous stream of a channel.</summary>
    /// <param name="number">Channel number.</param>
    /// <param name="k">The stream key.</param>
    /// <returns>A task that completes when the client disconnects.</returns>
    [HttpGet("live.ts")]
    public async Task Live([FromRoute] string number, [FromQuery] string? k)
    {
        if (!_token.IsValid(k))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var session = _sessions.Create(number);
        var tmpDir = Path.Combine(Plugin.Instance!.WorkDir, "tmp");
        Directory.CreateDirectory(tmpDir);
        var concat = Path.Combine(tmpDir, session.Id + ".ffconcat");
        var itemUrl = $"{ServerUrl.Current}/PortalitoTV/channel/{number}/item.ts?k={k}&s={session.Id}";
        var sb = new StringBuilder("ffconcat version 1.0\n");
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("file '").Append(itemUrl).Append("'\n");
        }

        await System.IO.File.WriteAllTextAsync(concat, sb.ToString()).ConfigureAwait(false);

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "video/mp2t";
        _logger.LogInformation("Channel {Number}: stream {Session} started", number, session.Id);
        try
        {
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-fflags", "+genpts+discardcorrupt",
                "-f", "concat", "-safe", "0", "-protocol_whitelist", "file,http,https,tcp,tls",
                "-i", concat,
                "-map", "0", "-c", "copy",
                "-muxdelay", "0", "-muxpreload", "0",
                "-f", "mpegts", "pipe:1",
            };
            await FfmpegRunner.RunToStreamAsync(_encoder.EncoderPath, args, Response.Body, _logger, "channel " + number,
                HttpContext.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            _sessions.Remove(session.Id);
            System.IO.File.Delete(concat);
            _logger.LogInformation("Channel {Number}: stream {Session} ended", number, session.Id);
        }
    }

    /// <summary>The next program of a session: what the grid has from the session's cursor to the end of that program.</summary>
    /// <param name="number">Channel number.</param>
    /// <param name="k">The stream key.</param>
    /// <param name="s">The session id.</param>
    /// <returns>A task that completes when the program has been sent.</returns>
    [HttpGet("item.ts")]
    public async Task Item([FromRoute] string number, [FromQuery] string? k, [FromQuery] string? s)
    {
        var session = s is null ? null : _sessions.Get(s);
        if (!_token.IsValid(k) || session is null)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var now = DateTime.UtcNow;
        var t = session.CursorUtc is { } c && c > now - MaxLag ? c : now;
        var item = _schedule.At(number, t);

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "video/mp2t";
        var ct = HttpContext.RequestAborted;

        if (item is null || item.IsSlate)
        {
            if (item is not null)
            {
                var slate = item.EndUtc - t;
                session.CursorUtc = item.EndUtc;
                await _transcoder.StreamSlateAsync(slate, Response.Body, ct).ConfigureAwait(false);
                return;
            }

            session.CursorUtc = t + RetryAfter;
            await _transcoder.StreamSlateAsync(RetryAfter, Response.Body, ct).ConfigureAwait(false);
            return;
        }

        var offset = item.InPoint + (t - item.StartUtc);
        var length = item.EndUtc - t;
        session.CursorUtc = item.EndUtc;
        _logger.LogInformation("Channel {Number}: {Title} {Episode} from {Offset:hh\\:mm\\:ss} for {Length:hh\\:mm\\:ss}",
            number, item.Title, item.EpisodeTitle, offset, length);

        // Initial burst = what is missing to run 15 s ahead of real time (it doesn't build up between items).
        var burst = Math.Clamp(MaxLead.TotalSeconds - (t - now).TotalSeconds, 0, MaxLead.TotalSeconds);
        if (await _transcoder.StreamItemAsync(item, offset, length, burst, Response.Body, ct).ConfigureAwait(false)
            || ct.IsCancellationRequested)
        {
            return;
        }

        if (Response.HasStarted)
        {
            // It was cut mid-way (the portal stopped answering): the next item.ts resumes this program at the point that
            // matches the current time.
            session.CursorUtc = DateTime.UtcNow;
            return;
        }

        // It failed before sending anything: a slate, and the same program is retried.
        var retry = TimeSpan.FromTicks(Math.Min(RetryAfter.Ticks, length.Ticks));
        session.CursorUtc = t + retry;
        await _transcoder.StreamSlateAsync(retry, Response.Body, ct).ConfigureAwait(false);
    }
}
