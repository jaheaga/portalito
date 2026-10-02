using Jellyfin.Plugin.Portalito.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Api;

/// <summary>
/// The in-plugin proxy endpoints ffmpeg plays from. They are anonymous because ffmpeg cannot present a Jellyfin API key;
/// every URL is therefore signed and expiring (see <see cref="Proxy.ProxyUrlSigner"/>).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Portalito")]
public class PortalitoProxyController : ControllerBase
{
    private const string MpegUrl = "application/vnd.apple.mpegurl";
    private const string MpegTs = "video/mp2t";

    private readonly IPortalitoServicesProvider _provider;
    private readonly ILogger<PortalitoProxyController> _logger;

    public PortalitoProxyController(IPortalitoServicesProvider provider, ILogger<PortalitoProxyController> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    [HttpGet("live/{channel}.m3u8")]
    public Task<IActionResult> LivePlaylist(string channel, [FromQuery] long e, [FromQuery] string? s, CancellationToken cancellationToken)
        => Guard(
            async () =>
            {
                var services = _provider.Get();
                if (!services.Signer.VerifyLivePlaylist(channel, e, s))
                {
                    return StatusCode(StatusCodes.Status403Forbidden);
                }

                var playlist = await services.Proxy.GetLivePlaylistAsync(channel, cancellationToken).ConfigureAwait(false);
                if (playlist.Body is null)
                {
                    // 409 = the shared license is playing on another device; distinct from a plain CDN failure so the
                    // log says why a channel that worked yesterday won't start today.
                    if (playlist.Status == System.Net.HttpStatusCode.Conflict)
                    {
                        _logger.LogWarning("live/{Channel}: license in use elsewhere (409); every CDN refused", channel);
                    }
                    else
                    {
                        _logger.LogWarning("live/{Channel}: no CDN served a playlist, answering {Status}", channel, (int)playlist.Status);
                    }

                    return StatusCode((int)playlist.Status);
                }

                Response.Headers.CacheControl = "no-store";
                return Content(playlist.Body, MpegUrl);
            },
            cancellationToken);

    // ".ts" in the PATH: FFmpeg 7.1's HLS demuxer refuses segment URLs whose path has no media extension
    // ("... is not in allowed_segment_extensions", measured live 2026-09-23 with Jellyfin's ffmpeg 7.1.4),
    // and it checks the path, not the query string that carries the real upstream URL.
    [HttpGet("seg.ts")]
    public Task<IActionResult> Segment([FromQuery] string? c, [FromQuery] string? u, [FromQuery] long e, [FromQuery] string? s, CancellationToken cancellationToken)
        => Guard(
            async () =>
            {
                var services = _provider.Get();
                if (string.IsNullOrEmpty(c) || string.IsNullOrEmpty(u) || !services.Signer.VerifySegment(c, u, e, s))
                {
                    return StatusCode(StatusCodes.Status403Forbidden);
                }

                if (!Uri.TryCreate(u, UriKind.Absolute, out var upstreamUri))
                {
                    return BadRequest();
                }

                var upstream = await services.Proxy.OpenSegmentAsync(c, upstreamUri, cancellationToken).ConfigureAwait(false);
                return await RelayAsync(upstream, MpegTs, forwardRangeHeaders: false, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    [HttpGet("vod/{contentId}")]
    [HttpHead("vod/{contentId}")]
    public Task<IActionResult> Vod(string contentId, [FromQuery] string? series, [FromQuery] long e, [FromQuery] string? s, CancellationToken cancellationToken)
        => Guard(
            async () =>
            {
                var services = _provider.Get();
                var seriesId = series ?? string.Empty;
                return services.Signer.VerifyVod(contentId, seriesId, e, s)
                    ? await RelayVodAsync(services, contentId, seriesId, cancellationToken).ConfigureAwait(false)
                    : StatusCode(StatusCodes.Status403Forbidden);
            },
            cancellationToken);

    /// <summary>The same stream as <see cref="Vod"/>, under the never-expiring signature the "Siguiendo" .strm files carry.</summary>
    [HttpGet("play/{contentId}")]
    [HttpHead("play/{contentId}")]
    public Task<IActionResult> Play(string contentId, [FromQuery] string? series, [FromQuery] string? s, CancellationToken cancellationToken)
        => Guard(
            async () =>
            {
                var services = _provider.Get();
                var seriesId = series ?? string.Empty;
                return services.Signer.VerifyPlay(contentId, seriesId, s)
                    ? await RelayVodAsync(services, contentId, seriesId, cancellationToken).ConfigureAwait(false)
                    : StatusCode(StatusCodes.Status403Forbidden);
            },
            cancellationToken);

    private async Task<IActionResult> RelayVodAsync(PortalitoServices services, string contentId, string seriesId, CancellationToken cancellationToken)
    {
        var method = HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        var upstream = await services.Proxy.OpenVodAsync(
            contentId,
            seriesId,
            method,
            Request.Headers.Range.ToString(),
            Request.Headers.IfRange.ToString(),
            cancellationToken).ConfigureAwait(false);
        return await RelayAsync(upstream, "video/mp4", forwardRangeHeaders: true, cancellationToken).ConfigureAwait(false);
    }

    [HttpGet("img")]
    public Task<IActionResult> Image([FromQuery] string? u, [FromQuery] long e, [FromQuery] string? s, CancellationToken cancellationToken)
        => Guard(
            async () =>
            {
                var services = _provider.Get();
                if (string.IsNullOrEmpty(u) || !services.Signer.VerifyImage(u, e, s))
                {
                    return StatusCode(StatusCodes.Status403Forbidden);
                }

                if (!Uri.TryCreate(u, UriKind.Absolute, out var upstreamUri))
                {
                    return BadRequest();
                }

                var upstream = await services.Proxy.OpenImageAsync(upstreamUri, cancellationToken).ConfigureAwait(false);
                return await RelayImageAsync(upstream, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Echoes a signed nonce, for the config page's connection test (see <see cref="Proxy.ProxyUrlSigner.PingUrl"/>).</summary>
    [HttpGet("ping")]
    public Task<IActionResult> Ping([FromQuery] string? n, [FromQuery] long e, [FromQuery] string? s, CancellationToken cancellationToken)
        => Guard(
            () =>
            {
                var services = _provider.Get();
                IActionResult result = string.IsNullOrEmpty(n) || !services.Signer.VerifyPing(n, e, s)
                    ? StatusCode(StatusCodes.Status403Forbidden)
                    : Content(n, "text/plain");
                return Task.FromResult(result);
            },
            cancellationToken);

    // Separate from RelayAsync: images need their Content-Type normalized (the CDN's "image/jpg" breaks
    // Jellyfin's own image cache/converter -- see ProxyUrlSigner.ImageUrl), not forwarded verbatim like video's is.
    /// <summary>The largest poster relayed; anything bigger isn't a poster (an HTML error page, a misnamed file).</summary>
    internal const long MaxImageBytes = 10 * 1024 * 1024;

    private async Task<IActionResult> RelayImageAsync(HttpResponseMessage upstream, CancellationToken cancellationToken)
    {
        Response.RegisterForDispose(upstream);
        if (upstream.Content.Headers.ContentLength > MaxImageBytes)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        Response.StatusCode = (int)upstream.StatusCode;

        if (Proxy.ImageTypes.Normalize(upstream.Content.Headers.ContentType?.MediaType) is not { } contentType)
        {
            // Missing or vague ("image/*"): buffer the (small) poster -- bounded -- and tell its type from its first bytes.
            var bytes = await ReadBoundedAsync(upstream.Content, MaxImageBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                Response.StatusCode = StatusCodes.Status502BadGateway;
                return new EmptyResult();
            }

            Response.ContentType = Proxy.ImageTypes.Sniff(bytes);
            Response.ContentLength = bytes.Length;
            await Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return new EmptyResult();
        }

        Response.ContentType = contentType;
        if (upstream.Content.Headers.ContentLength is { } length)
        {
            Response.ContentLength = length;
        }

        await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await body.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>The body, or null when it's longer than <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        await using var body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<IActionResult> RelayAsync(HttpResponseMessage upstream, string contentType, bool forwardRangeHeaders, CancellationToken cancellationToken)
    {
        // ffmpeg (transcoding, not just relaying) can read this response far slower and burstier than a plain
        // client -- Kestrel's default MinResponseDataRate (240 B/s, enforced per write, not averaged) silently
        // aborts a slow write and truncates the stream with no server-side exception. Measured live 2026-09-21:
        // VOD playback stopped cleanly (ffmpeg exit code 0, not a crash) at inconsistent, short points (49s once,
        // ~6 min another time), while a plain curl through this SAME proxy pulled the identical 976MB file
        // completely in 11s -- proving the proxy's own body copy and the CDN's file are both fine; only a slow
        // CONSUMER trips this. Safe to disable here: this endpoint only ever serves already-authenticated,
        // signed, short-lived URLs to ffmpeg itself, not arbitrary internet clients, so the slow-loris protection
        // this feature exists for doesn't apply.
        var minRate = HttpContext.Features.Get<IHttpMinResponseDataRateFeature>();
        if (minRate is not null)
        {
            minRate.MinDataRate = null;
        }

        Response.RegisterForDispose(upstream);
        Response.StatusCode = (int)upstream.StatusCode;

        var headers = upstream.Content.Headers;
        Response.ContentType = forwardRangeHeaders ? headers.ContentType?.ToString() ?? contentType : contentType;
        if (headers.ContentLength is { } length)
        {
            Response.ContentLength = length;
        }

        if (forwardRangeHeaders)
        {
            if (headers.ContentRange is { } contentRange)
            {
                Response.Headers.ContentRange = contentRange.ToString();
            }

            if (upstream.Headers.AcceptRanges.Count > 0)
            {
                Response.Headers.AcceptRanges = string.Join(", ", upstream.Headers.AcceptRanges);
            }

            // The validators a player needs to send a meaningful If-Range (which is forwarded upstream).
            if (upstream.Headers.ETag is { } etag)
            {
                Response.Headers.ETag = etag.ToString();
            }

            if (headers.LastModified is { } lastModified)
            {
                Response.Headers.LastModified = lastModified.ToString("R");
            }
        }

        if (!HttpMethods.IsHead(Request.Method))
        {
            await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await body.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }

        return new EmptyResult();
    }

    // Maps failures to gateway-style statuses. Once the body has started streaming the status can no longer change, so
    // those exceptions propagate and Kestrel aborts the connection.
    private async Task<IActionResult> Guard(Func<Task<IActionResult>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        // Every failure is logged with its reason: ffmpeg only ever reports the bare status, which is all the
        // server log had to go on when live playback broke (a 503 with the portal's reason nowhere to be seen).
        catch (ArgumentException ex) when (!Response.HasStarted)
        {
            _logger.LogWarning("{Path}: bad request ({Reason})", Request.Path, ex.Message);
            return BadRequest();
        }
        catch (PortalException ex) when (!Response.HasStarted)
        {
            _logger.LogWarning("{Path}: portal failure, answering 503 ({Reason})", Request.Path, ex.Message);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException ex) when (!Response.HasStarted)
        {
            _logger.LogWarning("{Path}: upstream unreachable, answering 502 ({Reason})", Request.Path, ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !Response.HasStarted)
        {
            _logger.LogWarning("{Path}: upstream timed out, answering 504", Request.Path);
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }
}
