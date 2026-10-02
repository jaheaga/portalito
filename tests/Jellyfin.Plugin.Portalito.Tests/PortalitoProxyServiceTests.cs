using System.Net;
using System.Text;
using Jellyfin.Plugin.Portalito.Crypto;
using Jellyfin.Plugin.Portalito.Portal;
using Jellyfin.Plugin.Portalito.Proxy;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

internal sealed class FakeUpstream : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = new();

    public Func<CapturedRequest, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers);
        Requests.Add(captured);
        return Task.FromResult(Respond(captured));
    }
}

internal sealed class FakeResolver : IStreamResolver
{
    public int LiveCalls { get; private set; }

    public int VodCalls { get; private set; }

    public string? LastVodSeries { get; private set; }

    public Func<int, LiveStream> Live { get; set; } = _ => throw new InvalidOperationException("no live session scripted");

    public Func<int, VodStream> Vod { get; set; } = _ => throw new InvalidOperationException("no vod session scripted");

    public Task<LiveStream> ResolveLiveAsync(string channelCode, CancellationToken cancellationToken)
        => Task.FromResult(Live(++LiveCalls));

    public Task<VodStream> ResolveVodAsync(string contentId, string seriesContentId, CancellationToken cancellationToken)
    {
        LastVodSeries = seriesContentId;
        return Task.FromResult(Vod(++VodCalls));
    }
}

public class PortalitoProxyServiceTests
{
    // Synthetic token + moment; expected Content-Auth is computed from the same test signer, never a captured value.
    private const string Token = "00112233445566778899aabbccddeeff";
    private const long KatMoment = 1786228951248L;
    private const string BaseAuth = "user_id=1&sign_type=cfl&token=" + Token;
    private static readonly IContentAuthSigner ContentAuth = TestContentAuth.Signer();
    private const string ProxyBase = "http://127.0.0.1:8096";

    private readonly ManualClock _clock = new(DateTimeOffset.FromUnixTimeMilliseconds(KatMoment));
    private readonly FakeUpstream _upstream = new();
    private readonly FakeResolver _resolver = new();
    private readonly ProxyUrlSigner _signer;
    private readonly PortalitoProxyService _service;

    public PortalitoProxyServiceTests()
    {
        _signer = new ProxyUrlSigner("unit-test-secret", ProxyBase, _clock);
        _service = new PortalitoProxyService(
            new HttpClient(_upstream),
            _resolver,
            _signer,
            ContentAuth,
            new ProxyIdentity("com.example.app", "12345", "ua"),
            _clock);
        _resolver.Live = _ => new LiveStream("chan1", "cfl.test", BaseAuth, "LIC-live", Token, null);
        _resolver.Vod = _ => new VodStream("MEDIA123", "vod.test", "vod-auth-base", "LIC-vod", null);
    }

    private static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8) };

    // ---- live playlist ----

    [Fact]
    public async Task Playlist_is_fetched_with_the_kat_signed_content_auth_and_player_headers()
    {
        _upstream.Respond = _ => Text("#EXTM3U\n");

        await _service.GetLivePlaylistAsync("chan1", default);

        var req = Assert.Single(_upstream.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("http://cfl.test/live/chan1.m3u8", req.Uri.ToString());
        Assert.Equal(
            ContentAuth.BuildContentAuth(BaseAuth, Token, KatMoment),
            req.Header("Content-Auth"));
        Assert.Equal("LIC-live", req.Header("Content-License"));
        Assert.Equal("ua", req.Header("User-Agent"));
        Assert.Equal("com.example.app", req.Header("App"));
        Assert.Equal("12345", req.Header("App-Version"));
        Assert.Equal("0", req.Header("X-Buffer"));
    }

    [Fact]
    public async Task Playlist_is_requested_under_the_signals_play_code_not_the_channel_code()
    {
        // The license authorizes the signal named by playCode; asking for the channel code with it is a 401
        // (the reference client, measured: cx-EXAMPLE is served as cx-2EF7E10E40C1ac19D6A9F3ED4CD2).
        _resolver.Live = _ => new LiveStream("cx-EXAMPLE", "cfl.test", BaseAuth, "LIC-live", Token, null, "cx-2EF7E10E40C1ac19D6A9F3ED4CD2");
        _upstream.Respond = _ => Text("#EXTM3U\n");

        await _service.GetLivePlaylistAsync("cx-EXAMPLE", default);

        Assert.Equal("http://cfl.test/live/cx-2EF7E10E40C1ac19D6A9F3ED4CD2.m3u8", Assert.Single(_upstream.Requests).Uri.ToString());
    }

    [Fact]
    public async Task Playlist_segments_are_rewritten_to_verifiable_proxy_urls()
    {
        _upstream.Respond = _ => Text(
            "#EXTM3U\n#EXTINF:4,\nhttp://tdgao.test/live/chan1/a.ts\n#EXTINF:4,\nhttp://nmbde.test/live/chan1/b.ts\n");

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        var lines = result.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var segLines = lines.Where(l => l.StartsWith(ProxyBase, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, segLines.Length);
        Assert.DoesNotContain(lines, l => l.StartsWith("http://tdgao", StringComparison.Ordinal) || l.StartsWith("http://nmbde", StringComparison.Ordinal));

        var q = System.Web.HttpUtility.ParseQueryString(new Uri(segLines[0]).Query);
        Assert.Equal("http://tdgao.test/live/chan1/a.ts", q["u"]);
        Assert.True(_signer.VerifySegment(q["c"]!, q["u"]!, long.Parse(q["e"]!), q["s"]));
    }

    [Fact]
    public async Task Playlist_session_is_cached_across_fetches()
    {
        _upstream.Respond = _ => Text("#EXTM3U\n");

        await _service.GetLivePlaylistAsync("chan1", default);
        await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(1, _resolver.LiveCalls);
        Assert.Equal(2, _upstream.Requests.Count);
    }

    [Fact]
    public async Task Playlist_upstream_error_status_is_returned_without_a_body()
    {
        _upstream.Respond = _ => Text("nope", HttpStatusCode.NotFound);

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task Playlist_401_re_resolves_once_and_retries()
    {
        _resolver.Live = n => new LiveStream("chan1", "cfl" + n + ".test", BaseAuth, "LIC" + n, Token, null);
        _upstream.Respond = r => r.Uri.Host == "cfl1.test" ? Text("", HttpStatusCode.Unauthorized) : Text("#EXTM3U\n");

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(2, _resolver.LiveCalls);
        Assert.Equal(new[] { "cfl1.test", "cfl2.test" }, _upstream.Requests.Select(r => r.Uri.Host));
        Assert.Equal("LIC2", _upstream.Requests[1].Header("Content-License"));
    }

    [Fact]
    public async Task Playlist_persistent_401_gives_up_after_one_retry()
    {
        _upstream.Respond = _ => Text("", HttpStatusCode.Unauthorized);

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Equal(2, _resolver.LiveCalls);
        Assert.Equal(2, _upstream.Requests.Count);
    }

    [Fact]
    public async Task Playlist_fails_over_to_an_alternate_cdn_within_one_session()
    {
        _resolver.Live = _ => new LiveStream("chan1", "cfl1.test", BaseAuth, "LIC-live", Token, null, null,
            new[] { new LiveCdn("cfl2.test", BaseAuth, Token) });
        _upstream.Respond = r => r.Uri.Host == "cfl1.test" ? Text("", HttpStatusCode.NotFound) : Text("#EXTM3U\n");

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(1, _resolver.LiveCalls); // one session, not a re-resolve
        Assert.Equal(new[] { "cfl1.test", "cfl2.test" }, _upstream.Requests.Select(r => r.Uri.Host));
    }

    [Fact]
    public async Task Playlist_fails_over_past_a_cdn_that_cannot_be_reached()
    {
        // DNS failure / connection refused: an exception, not a status. It used to escape the failover loop and
        // answer 502 without ever trying the second CDN.
        _resolver.Live = _ => new LiveStream("chan1", "cfl1.test", BaseAuth, "LIC-live", Token, null, null,
            new[] { new LiveCdn("cfl2.test", BaseAuth, Token) });
        _upstream.Respond = r => r.Uri.Host == "cfl1.test" ? throw new HttpRequestException("connection refused") : Text("#EXTM3U\n");

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(new[] { "cfl1.test", "cfl2.test" }, _upstream.Requests.Select(r => r.Uri.Host));
    }

    [Fact]
    public async Task Playlist_fails_over_past_a_cdn_that_times_out()
    {
        // SocketsHttpHandler's ConnectTimeout surfaces as a TaskCanceledException the caller didn't ask for.
        _resolver.Live = _ => new LiveStream("chan1", "cfl1.test", BaseAuth, "LIC-live", Token, null, null,
            new[] { new LiveCdn("cfl2.test", BaseAuth, Token) });
        _upstream.Respond = r => r.Uri.Host == "cfl1.test" ? throw new TaskCanceledException("connect timed out") : Text("#EXTM3U\n");

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(2, _upstream.Requests.Count);
    }

    [Fact]
    public async Task Playlist_with_every_cdn_unreachable_reports_a_gateway_status_instead_of_throwing()
    {
        _upstream.Respond = _ => throw new HttpRequestException("no route to host");

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Null(result.Body);
        Assert.Equal(HttpStatusCode.BadGateway, result.Status);
        Assert.Equal(1, _resolver.LiveCalls);
    }

    [Fact]
    public async Task Playlist_cancelled_by_the_player_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        _upstream.Respond = _ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GetLivePlaylistAsync("chan1", cts.Token));
    }

    [Fact]
    public async Task Master_playlist_is_followed_to_its_best_variant_and_relative_segments_resolve_against_it()
    {
        _upstream.Respond = r => r.Uri.AbsolutePath switch
        {
            "/live/chan1.m3u8" => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000\nlo/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000\nhi/index.m3u8\n"),
            "/live/hi/index.m3u8" => Text("#EXTM3U\n#EXTINF:4,\nseg1.ts\n"),
            _ => Text("", HttpStatusCode.NotFound),
        };

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(new[] { "/live/chan1.m3u8", "/live/hi/index.m3u8" }, _upstream.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.NotNull(_upstream.Requests[1].Header("Content-Auth")); // the variant is signed like the master
        var seg = result.Body!.Split('\n').Single(l => l.StartsWith(ProxyBase, StringComparison.Ordinal));
        Assert.Equal("http://cfl.test/live/hi/seg1.ts", System.Web.HttpUtility.ParseQueryString(new Uri(seg).Query)["u"]);
    }

    [Fact]
    public async Task Playlist_remembers_the_cdn_that_worked_and_tries_it_first_next_time()
    {
        _resolver.Live = _ => new LiveStream("chan1", "cfl1.test", BaseAuth, "LIC-live", Token, null, null,
            new[] { new LiveCdn("cfl2.test", BaseAuth, Token) });
        _upstream.Respond = r => r.Uri.Host == "cfl1.test" ? Text("", HttpStatusCode.NotFound) : Text("#EXTM3U\n");

        await _service.GetLivePlaylistAsync("chan1", default);
        _upstream.Requests.Clear();
        await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal("cfl2.test", Assert.Single(_upstream.Requests).Uri.Host);
    }

    [Fact]
    public async Task Playlist_200_that_is_not_a_playlist_is_treated_as_a_failure()
    {
        // A Cloudflare error page served as 200 must not be handed to the player as a playlist.
        _upstream.Respond = _ => Text("<html>error</html>");

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Null(result.Body);
        Assert.Equal(HttpStatusCode.BadGateway, result.Status);
        Assert.Equal(1, _resolver.LiveCalls);
    }

    [Fact]
    public async Task Playlist_409_re_resolves_once_then_reports_the_conflict()
    {
        _upstream.Respond = _ => Text("", HttpStatusCode.Conflict);

        var result = await _service.GetLivePlaylistAsync("chan1", default);

        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal(2, _resolver.LiveCalls);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("a?b")]
    [InlineData("")]
    public async Task Unsafe_channel_ids_are_refused_before_any_upstream_call(string channel)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetLivePlaylistAsync(channel, default));
        Assert.Empty(_upstream.Requests);
        Assert.Equal(0, _resolver.LiveCalls);
    }

    // ---- live segments ----

    [Fact]
    public async Task Each_segment_request_gets_a_fresh_start_moment_and_matching_sign2()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x47, 1, 2 }) };
        var seg = new Uri("http://tdgao.test/live/chan1/a.ts");

        using (await _service.OpenSegmentAsync("chan1", seg, default))
        {
        }

        _clock.Now = _clock.Now.AddMilliseconds(1234);
        using (await _service.OpenSegmentAsync("chan1", seg, default))
        {
        }

        Assert.Equal(2, _upstream.Requests.Count);
        Assert.Equal(seg, _upstream.Requests[0].Uri);
        Assert.Equal(
            ContentAuth.BuildContentAuth(BaseAuth, Token, KatMoment),
            _upstream.Requests[0].Header("Content-Auth"));

        var secondMoment = KatMoment + 1234;
        Assert.Equal(
            ContentAuth.BuildContentAuth(BaseAuth, Token, secondMoment),
            _upstream.Requests[1].Header("Content-Auth"));
        Assert.NotEqual(_upstream.Requests[0].Header("Content-Auth"), _upstream.Requests[1].Header("Content-Auth"));
        Assert.Equal(1, _resolver.LiveCalls);
    }

    [Fact]
    public async Task Segment_body_streams_through()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x47, 1, 2, 3 }) };

        using var response = await _service.OpenSegmentAsync("chan1", new Uri("http://h.test/a.ts"), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 0x47, 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Segment_401_re_resolves_and_retries_with_the_new_session()
    {
        _resolver.Live = n => new LiveStream("chan1", "cfl.test", BaseAuth, "LIC" + n, Token, null);
        _upstream.Respond = r => r.Header("Content-License") == "LIC1"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x47 }) };

        using var response = await _service.OpenSegmentAsync("chan1", new Uri("http://h.test/a.ts"), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _resolver.LiveCalls);
    }

    [Fact]
    public async Task Segment_retries_a_not_yet_published_edge_segment_then_succeeds()
    {
        var calls = 0;
        _upstream.Respond = _ => ++calls == 1
            ? Text("", HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x47 }) };

        using var response = await _service.OpenSegmentAsync("chan1", new Uri("http://h.test/a.ts"), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _upstream.Requests.Count);
    }

    [Fact]
    public async Task Segment_retries_a_dropped_connection_then_succeeds()
    {
        var calls = 0;
        _upstream.Respond = _ => ++calls == 1
            ? throw new HttpRequestException("connection reset")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x47 }) };

        using var response = await _service.OpenSegmentAsync("chan1", new Uri("http://h.test/a.ts"), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Segment_whose_connection_keeps_failing_throws_after_three_attempts()
    {
        _upstream.Respond = _ => throw new HttpRequestException("connection reset");

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.OpenSegmentAsync("chan1", new Uri("http://h.test/a.ts"), default));
        Assert.Equal(3, _upstream.Requests.Count);
    }

    [Fact]
    public async Task Segment_that_stays_404_gives_up_after_three_attempts()
    {
        _upstream.Respond = _ => Text("", HttpStatusCode.NotFound);

        using var response = await _service.OpenSegmentAsync("chan1", new Uri("http://h.test/a.ts"), default);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(3, _upstream.Requests.Count);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://h.test/a.ts")]
    public async Task Segment_urls_must_be_http(string url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.OpenSegmentAsync("chan1", new Uri(url), default));
        Assert.Empty(_upstream.Requests);
    }

    // ---- VOD ----

    [Fact]
    public async Task Vod_forwards_range_and_identity_headers_without_signing()
    {
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[100]) };
            r.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(100, 199, 5000);
            return r;
        };

        using var response = await _service.OpenVodAsync("CONTENT1", "SERIES1", HttpMethod.Get, "bytes=100-199", "\"etag\"", default);

        var req = Assert.Single(_upstream.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("http://vod.test/vod/MEDIA123_media.mp4", req.Uri.ToString());
        Assert.Equal("vod-auth-base", req.Header("Content-Auth"));
        Assert.Equal("LIC-vod", req.Header("Content-License"));
        Assert.Equal("ua", req.Header("User-Agent"));
        Assert.Equal("com.example.app", req.Header("App"));
        Assert.Equal("12345", req.Header("App-Version"));
        Assert.Equal("bytes=100-199", req.Header("Range"));
        Assert.Equal("\"etag\"", req.Header("If-Range"));
        Assert.Null(req.Header("X-Buffer"));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(5000, response.Content.Headers.ContentRange!.Length);
        Assert.Equal("SERIES1", _resolver.LastVodSeries);
    }

    [Fact]
    public async Task Vod_without_range_sends_no_range_header_and_supports_head()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

        using var response = await _service.OpenVodAsync("CONTENT1", string.Empty, HttpMethod.Head, null, null, default);

        var req = Assert.Single(_upstream.Requests);
        Assert.Equal(HttpMethod.Head, req.Method);
        Assert.Null(req.Header("Range"));
        Assert.Null(req.Header("If-Range"));
    }

    [Fact]
    public async Task Vod_retries_a_connection_that_timed_out_with_the_same_range()
    {
        var calls = 0;
        _upstream.Respond = _ => ++calls <= 2
            ? throw new TaskCanceledException("connect timed out")
            : new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[10]) };

        using var response = await _service.OpenVodAsync("CONTENT1", string.Empty, HttpMethod.Get, "bytes=142551000-", null, default);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(3, _upstream.Requests.Count);
        Assert.All(_upstream.Requests, r => Assert.Equal("bytes=142551000-", r.Header("Range")));
        Assert.Equal(1, _resolver.VodCalls);
    }

    [Fact]
    public async Task Vod_whose_connection_keeps_failing_gives_up_after_three_attempts()
    {
        _upstream.Respond = _ => throw new HttpRequestException("connection refused");

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.OpenVodAsync("CONTENT1", string.Empty, HttpMethod.Get, null, null, default));
        Assert.Equal(PortalitoProxyService.VodConnectAttempts, _upstream.Requests.Count);
    }

    [Fact]
    public async Task Vod_cancelled_by_the_player_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        _upstream.Respond = _ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("player went away");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.OpenVodAsync("CONTENT1", string.Empty, HttpMethod.Get, null, null, cts.Token));
        Assert.Single(_upstream.Requests);
    }

    [Fact]
    public async Task Vod_session_is_cached_per_content_and_series()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

        (await _service.OpenVodAsync("C1", string.Empty, HttpMethod.Get, null, null, default)).Dispose();
        (await _service.OpenVodAsync("C1", string.Empty, HttpMethod.Get, "bytes=0-9", null, default)).Dispose();
        Assert.Equal(1, _resolver.VodCalls);

        (await _service.OpenVodAsync("C1", "S1", HttpMethod.Get, null, null, default)).Dispose();
        Assert.Equal(2, _resolver.VodCalls);
    }

    [Fact]
    public async Task Vod_401_re_resolves_and_retries()
    {
        _resolver.Vod = n => new VodStream("MEDIA123", "vod" + n + ".test", "auth" + n, "LIC" + n, null);
        _upstream.Respond = r => new HttpResponseMessage(r.Uri.Host == "vod1.test" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);

        using var response = await _service.OpenVodAsync("C1", string.Empty, HttpMethod.Get, null, null, default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _resolver.VodCalls);
    }

    [Fact]
    public async Task Portal_failures_propagate_to_the_caller()
    {
        _resolver.Live = _ => throw new PortalException("portal down");

        await Assert.ThrowsAsync<PortalException>(() => _service.GetLivePlaylistAsync("chan1", default));
    }

    // ---- images ----

    [Fact]
    public async Task Image_is_fetched_with_no_auth_headers_at_all()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };

        using var response = await _service.OpenImageAsync(new Uri("https://cdn.test/poster.jpg"), default);

        var req = Assert.Single(_upstream.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://cdn.test/poster.jpg", req.Uri.ToString());
        Assert.Null(req.Header("Content-Auth"));
        Assert.Null(req.Header("Content-License"));
        Assert.Null(req.Header("User-Agent"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://h.test/a.jpg")]
    public async Task Image_urls_must_be_http(string url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.OpenImageAsync(new Uri(url), default));
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task A_lasting_409_re_resolves_at_most_once_per_backoff()
    {
        _upstream.Respond = _ => Text("", HttpStatusCode.Conflict);

        await _service.GetLivePlaylistAsync("chan1", default);
        Assert.Equal(2, _resolver.LiveCalls); // first conflict: one re-resolve

        await _service.GetLivePlaylistAsync("chan1", default);
        Assert.Equal(2, _resolver.LiveCalls); // within the backoff: the conflict is reported as-is

        _clock.Now += PortalitoProxyService.ConflictBackoff;
        await _service.GetLivePlaylistAsync("chan1", default);
        Assert.Equal(3, _resolver.LiveCalls);
    }

    [Theory]
    [InlineData("http://127.0.0.1/p.jpg", true)]
    [InlineData("http://localhost:8096/p.jpg", true)]
    [InlineData("http://192.168.0.10/p.jpg", true)]
    [InlineData("http://10.1.2.3/p.jpg", true)]
    [InlineData("http://172.20.0.2/p.jpg", true)]
    [InlineData("http://169.254.1.1/p.jpg", true)]
    [InlineData("http://[::1]/p.jpg", true)]
    [InlineData("http://nas.local/p.jpg", true)]
    [InlineData("https://cdn.example.test/p.jpg", false)]
    [InlineData("http://8.8.8.8/p.jpg", false)]
    public void Posters_on_this_machine_or_the_lan_are_refused(string url, bool local)
        => Assert.Equal(local, PortalitoProxyService.IsLocalHost(new Uri(url)));
}
