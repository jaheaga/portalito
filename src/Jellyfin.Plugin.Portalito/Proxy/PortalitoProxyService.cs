using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Portalito.Crypto;
using Jellyfin.Plugin.Portalito.Portal;

namespace Jellyfin.Plugin.Portalito.Proxy;

/// <summary>Identity headers the CDN expects alongside Content-Auth / Content-License.</summary>
public sealed record ProxyIdentity(string AppId, string AppVersion, string UserAgent);

public sealed record PlaylistResult(HttpStatusCode Status, string? Body);

/// <summary>
/// The re-signing proxy logic (port of reference/live_cfl.py, plus single-file VOD forwarding), independent of ASP.NET so
/// it can be unit-tested with a fake upstream. Live playlist and every segment get a fresh Content-Auth per request.
/// </summary>
public sealed partial class PortalitoProxyService
{
    /// <summary>How long a rewritten segment URL stays valid. Live segments expire upstream within seconds anyway.</summary>
    public static readonly TimeSpan SegmentUrlValidity = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long one CDN gets to serve the live playlist (a few KB) before the next CDN is tried. The shared client has
    /// no overall timeout (it streams whole VOD files), so without this a CDN that accepts the connection and then
    /// stalls would hold the player until it gave up.
    /// </summary>
    public static readonly TimeSpan PlaylistTimeout = TimeSpan.FromSeconds(10);

    // The player asks for the live edge segment just before the CDN publishes it, so a first 404/5xx is normal and
    // clears on a quick retry (the reference client: 3 attempts ~800ms apart).
    private const int SegmentAttempts = 3;
    private static readonly TimeSpan SegmentRetryDelay = TimeSpan.FromMilliseconds(800);

    // Remembers, per channel, which CDN last served a good playlist, so failover starts from it next time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastGoodCdn = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly IStreamResolver _resolver;
    private readonly ProxyUrlSigner _signer;
    private readonly IContentAuthSigner _contentAuth;
    private readonly ProxyIdentity _identity;
    private readonly TimeProvider _clock;
    private readonly StreamSessionCache<LiveStream> _live;
    private readonly StreamSessionCache<VodStream> _vod;

    public PortalitoProxyService(
        HttpClient http,
        IStreamResolver resolver,
        ProxyUrlSigner signer,
        IContentAuthSigner contentAuth,
        ProxyIdentity identity,
        TimeProvider clock)
    {
        _http = http;
        _resolver = resolver;
        _signer = signer;
        _contentAuth = contentAuth;
        _identity = identity;
        _clock = clock;
        var margin = TimeSpan.FromMinutes(5);
        var maxAge = TimeSpan.FromHours(2);
        _live = new StreamSessionCache<LiveStream>(clock, s => s.ExpiresAt, margin, maxAge);
        _vod = new StreamSessionCache<VodStream>(clock, s => s.ExpiresAt, margin, maxAge);
    }

    /// <summary>
    /// Fetches the live playlist with a signed Content-Auth and rewrites its segments to the local proxy. Fails over
    /// through every CDN the portal returned (starting from the one that last worked): a non-200, a 200 whose body
    /// isn't an <c>#EXTM3U</c> playlist (a Cloudflare error page counts as failed), or a CDN that can't be reached or
    /// doesn't answer within <see cref="PlaylistTimeout"/> moves to the next CDN. A rejected signature (401/403) or a
    /// 409 "license in use elsewhere" re-resolves the session once before giving up.
    /// </summary>
    public async Task<PlaylistResult> GetLivePlaylistAsync(string channel, CancellationToken cancellationToken)
    {
        RequireSafeId(channel);

        for (var round = 0; round < 2; round++)
        {
            var session = await _live.GetAsync(channel, ct => _resolver.ResolveLiveAsync(channel, ct), cancellationToken)
                .ConfigureAwait(false);

            var lastStatus = HttpStatusCode.BadGateway;
            var reResolve = false;
            foreach (var cdn in OrderCdns(channel, session.Cdns))
            {
                var playlistUri = new Uri($"http://{cdn.Host}/live/{Uri.EscapeDataString(session.CdnCode)}.m3u8");
                var (status, text, finalUri) = await FetchPlaylistAsync(playlistUri, cdn, session.License, cancellationToken).ConfigureAwait(false);

                if (text is not null)
                {
                    _lastGoodCdn[channel] = cdn.Host;
                    var body = HlsRewriter.Rewrite(text, finalUri, url => _signer.SegmentUrl(channel, url, SegmentUrlValidity));
                    return new PlaylistResult(HttpStatusCode.OK, body);
                }

                lastStatus = status;

                // A rejected signature or a shared-license conflict is about the session, not the CDN: re-resolve once.
                if (IsAuthFailure(status) || status == HttpStatusCode.Conflict)
                {
                    reResolve = true;
                    break;
                }

                // 404 / 5xx / unreachable / not a playlist: this CDN can't serve it right now; try the next.
            }

            if (round == 0 && reResolve)
            {
                _live.Invalidate(channel, session);
                continue;
            }

            return new PlaylistResult(lastStatus, null);
        }

        return new PlaylistResult(HttpStatusCode.BadGateway, null);
    }

    /// <summary>
    /// One CDN's media playlist: its text and the URL it came from (relative segment URIs resolve against it), or the
    /// status to report when it failed. A master playlist is followed to its highest-bandwidth variant on the same CDN.
    /// Network failures are failures of this CDN, not exceptions: the caller moves on to the next one.
    /// </summary>
    private async Task<(HttpStatusCode Status, string? Text, Uri Uri)> FetchPlaylistAsync(Uri uri, LiveCdn cdn, string license, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PlaylistTimeout);
        try
        {
            // At most one hop: a variant that is itself a master playlist is not followed further.
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                ApplyHeaders(request, SignedAuth(cdn), license, xBuffer: true);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (response.StatusCode, null, uri);
                }

                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (!IsPlaylist(text))
                {
                    return (HttpStatusCode.BadGateway, null, uri);
                }

                var variant = HlsRewriter.BestVariant(text, uri);
                if (variant is null)
                {
                    return (HttpStatusCode.OK, text, uri);
                }

                if (hop > 0)
                {
                    return (HttpStatusCode.BadGateway, null, uri);
                }

                uri = new Uri(variant);
            }
        }
        catch (HttpRequestException)
        {
            return (HttpStatusCode.BadGateway, null, uri);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The CDN's connect timeout, or PlaylistTimeout: a dead or stalled CDN.
            return (HttpStatusCode.GatewayTimeout, null, uri);
        }
    }

    /// <summary>
    /// Opens one live segment with a fresh Content-Auth, retrying a normal edge-not-yet-published 404/5xx a few times. The
    /// caller streams the body and disposes the response.
    /// </summary>
    public async Task<HttpResponseMessage> OpenSegmentAsync(string channel, Uri upstream, CancellationToken cancellationToken)
    {
        RequireSafeId(channel);
        if (upstream.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Segment URL must be http(s).", nameof(upstream));
        }

        var session = await _live.GetAsync(channel, ct => _resolver.ResolveLiveAsync(channel, ct), cancellationToken)
            .ConfigureAwait(false);
        // The playlist that produced this segment was served by whichever CDN's host it points at; sign with that
        // CDN's own auth/token (fall back to the primary if the host isn't one we know).
        var cdn = session.Cdns.FirstOrDefault(c => string.Equals(c.Host, upstream.Authority, StringComparison.OrdinalIgnoreCase))
            ?? session.Cdns[0];
        var reAuthed = false;

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, upstream);
            ApplyHeaders(request, SignedAuth(cdn), session.License, xBuffer: true);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt + 1 < SegmentAttempts
                && (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)))
            {
                // A dropped or refused connection is as transient as an edge 5xx: same retry budget.
                await Task.Delay(SegmentRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!reAuthed && IsAuthFailure(response.StatusCode))
            {
                response.Dispose();
                reAuthed = true;
                _live.Invalidate(channel, session);
                session = await _live.GetAsync(channel, ct => _resolver.ResolveLiveAsync(channel, ct), cancellationToken).ConfigureAwait(false);
                cdn = session.Cdns.FirstOrDefault(c => string.Equals(c.Host, upstream.Authority, StringComparison.OrdinalIgnoreCase)) ?? session.Cdns[0];
                continue;
            }

            if (response.IsSuccessStatusCode || attempt + 1 >= SegmentAttempts || !IsRetryableStatus(response.StatusCode))
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(SegmentRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Forwards a VOD file request, including HTTP Range so players can seek. VOD needs the auth and license but no
    /// per-request sign2 (it is a single file, not segments). The caller streams the body and disposes the response.
    /// </summary>
    public async Task<HttpResponseMessage> OpenVodAsync(
        string contentId,
        string seriesId,
        HttpMethod method,
        string? range,
        string? ifRange,
        CancellationToken cancellationToken)
    {
        RequireSafeId(contentId);
        var key = contentId + "|" + seriesId;

        for (var attempt = 0; ; attempt++)
        {
            var session = await _vod.GetAsync(key, ct => _resolver.ResolveVodAsync(contentId, seriesId, ct), cancellationToken)
                .ConfigureAwait(false);

            using var request = new HttpRequestMessage(method, session.MediaUri);
            ApplyHeaders(request, session.CflAuthUrl, session.License, xBuffer: false);
            if (!string.IsNullOrEmpty(range))
            {
                request.Headers.TryAddWithoutValidation("Range", range);
            }

            if (!string.IsNullOrEmpty(ifRange))
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (attempt == 0 && IsAuthFailure(response.StatusCode))
            {
                response.Dispose();
                _vod.Invalidate(key, session);
                continue;
            }

            return response;
        }
    }

    /// <summary>
    /// Fetches a poster from the portal's public image CDN. Unlike video, images need no Content-Auth/license --
    /// confirmed live 2026-09-23 (<c>curl -I</c> on a poster URL with no headers at all returns 200). The caller
    /// streams the body and disposes the response; the controller normalizes the CDN's non-standard
    /// <c>image/jpg</c> Content-Type on the way out.
    /// </summary>
    public async Task<HttpResponseMessage> OpenImageAsync(Uri upstream, CancellationToken cancellationToken)
    {
        if (upstream.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Image URL must be http(s).", nameof(upstream));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, upstream);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private string SignedAuth(LiveCdn cdn)
        => _contentAuth.BuildContentAuth(cdn.AuthUrl, cdn.Token, _clock.GetUtcNow().ToUnixTimeMilliseconds());

    /// <summary>The CDNs to try, with the one that last served this channel a good playlist moved to the front.</summary>
    private IReadOnlyList<LiveCdn> OrderCdns(string channel, IReadOnlyList<LiveCdn> cdns)
    {
        if (cdns.Count < 2 || !_lastGoodCdn.TryGetValue(channel, out var host))
        {
            return cdns;
        }

        var preferred = cdns.Where(c => c.Host == host).ToList();
        return preferred.Count == 0 ? cdns : preferred.Concat(cdns.Where(c => c.Host != host)).ToList();
    }

    // An HLS playlist starts with the #EXTM3U tag; a Cloudflare error page served as 200 does not.
    private static bool IsPlaylist(string body) => body.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);

    // Statuses worth a quick retry: the edge segment isn't published yet (404), or the CDN is briefly unhappy (5xx / 429).
    private static bool IsRetryableStatus(HttpStatusCode status)
        => status is HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private void ApplyHeaders(HttpRequestMessage request, string contentAuth, string license, bool xBuffer)
    {
        request.Headers.TryAddWithoutValidation("Content-Auth", contentAuth);
        request.Headers.TryAddWithoutValidation("Content-License", license);
        request.Headers.TryAddWithoutValidation("User-Agent", _identity.UserAgent);
        request.Headers.TryAddWithoutValidation("App", _identity.AppId);
        request.Headers.TryAddWithoutValidation("App-Version", _identity.AppVersion);
        if (xBuffer)
        {
            request.Headers.TryAddWithoutValidation("X-Buffer", "0");
        }
    }

    private static bool IsAuthFailure(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    // Ids end up in an upstream URL path, so refuse anything that could change the path.
    private static void RequireSafeId(string id)
    {
        if (!SafeIdRegex().IsMatch(id))
        {
            throw new ArgumentException("Unsafe id.", nameof(id));
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex SafeIdRegex();
}
