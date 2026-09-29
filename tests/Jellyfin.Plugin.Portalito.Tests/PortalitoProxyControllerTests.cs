using System.Net;
using System.Text;
using Jellyfin.Plugin.Portalito.Api;
using Jellyfin.Plugin.Portalito.Portal;
using Jellyfin.Plugin.Portalito.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class PortalitoProxyControllerTests
{
    private const string Token = "00112233445566778899aabbccddeeff";
    private const string ProxyBase = "http://127.0.0.1:8096";

    private readonly ManualClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000));
    private readonly FakeUpstream _upstream = new();
    private readonly FakeResolver _resolver = new();
    private readonly ProxyUrlSigner _signer;
    private readonly PortalitoProxyController _controller;
    private readonly MemoryStream _body = new();

    public PortalitoProxyControllerTests()
    {
        _signer = new ProxyUrlSigner("unit-test-secret", ProxyBase, _clock);
        var proxy = new PortalitoProxyService(new HttpClient(_upstream), _resolver, _signer, TestContentAuth.Signer(), new ProxyIdentity("app", "1", "ua"), _clock);
        var services = new PortalitoServices(null!, _signer, proxy, null!);
        _controller = new PortalitoProxyController(new StubProvider(() => services), NullLogger<PortalitoProxyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        _controller.HttpContext.Response.Body = _body;
        _resolver.Live = _ => new LiveStream("chan1", "cfl.test", "base&token=" + Token, "LIC", Token, null);
        _resolver.Vod = _ => new VodStream("MEDIA1", "vod.test", "vod-auth", "LIC-vod", null);
    }

    private sealed class StubProvider : IPortalitoServicesProvider
    {
        private readonly Func<PortalitoServices> _get;

        public StubProvider(Func<PortalitoServices> get) => _get = get;

        public PortalitoServices Get() => _get();
    }

    private static (long E, string S) Params(string url)
    {
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        return (long.Parse(q["e"]!), q["s"]!);
    }

    private static int Status(IActionResult result) => result switch
    {
        StatusCodeResult s => s.StatusCode,
        ContentResult c => c.StatusCode ?? 200,
        EmptyResult => 200,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private string BodyText() => Encoding.UTF8.GetString(_body.ToArray());

    // ---- live playlist ----

    [Fact]
    public async Task Live_playlist_with_a_valid_signature_returns_the_rewritten_playlist()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("#EXTM3U\n#EXTINF:4,\nhttp://seg.test/a.ts\n"),
        };
        var (e, s) = Params(_signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));

        var result = await _controller.LivePlaylist("chan1", e, s, default);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        Assert.Contains($"{ProxyBase}/Portalito/seg.ts?c=chan1", content.Content);
        Assert.DoesNotContain("http://seg.test/a.ts\n", content.Content);
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadbeef")]
    public async Task Live_playlist_with_a_bad_signature_is_forbidden_and_never_hits_upstream(string? signature)
    {
        var result = await _controller.LivePlaylist("chan1", _clock.Now.AddHours(1).ToUnixTimeSeconds(), signature, default);

        Assert.Equal(403, Status(result));
        Assert.Empty(_upstream.Requests);
        Assert.Equal(0, _resolver.LiveCalls);
    }

    [Fact]
    public async Task Live_playlist_with_an_expired_signature_is_forbidden()
    {
        var (e, s) = Params(_signer.LivePlaylistUrl("chan1", TimeSpan.FromMinutes(1)));
        _clock.Now = _clock.Now.AddMinutes(2);

        Assert.Equal(403, Status(await _controller.LivePlaylist("chan1", e, s, default)));
    }

    [Fact]
    public async Task Live_playlist_upstream_status_is_passed_through()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var (e, s) = Params(_signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));

        Assert.Equal(404, Status(await _controller.LivePlaylist("chan1", e, s, default)));
    }

    [Fact]
    public async Task Unsafe_channel_with_a_valid_signature_is_a_bad_request()
    {
        var (e, s) = Params(_signer.LivePlaylistUrl("../x", TimeSpan.FromHours(1)));

        Assert.Equal(400, Status(await _controller.LivePlaylist("../x", e, s, default)));
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Portal_failure_is_a_503()
    {
        _resolver.Live = _ => throw new PortalException("not configured");
        var (e, s) = Params(_signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));

        Assert.Equal(503, Status(await _controller.LivePlaylist("chan1", e, s, default)));
    }

    [Fact]
    public async Task Upstream_connection_failure_is_a_502()
    {
        _upstream.Respond = _ => throw new HttpRequestException("refused");
        var (e, s) = Params(_signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));

        Assert.Equal(502, Status(await _controller.LivePlaylist("chan1", e, s, default)));
    }

    [Fact]
    public async Task Provider_configuration_errors_are_a_503()
    {
        var controller = new PortalitoProxyController(new StubProvider(() => throw new PortalException("The 3DES key is not configured.")), NullLogger<PortalitoProxyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        Assert.Equal(503, Status(await controller.LivePlaylist("chan1", 1, "aa", default)));
    }

    // ---- segments ----

    [Fact]
    public async Task Segment_streams_the_upstream_body_as_mpegts()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x47, 9, 9 }) };
        const string seg = "http://seg.test/live/chan1/a.ts";
        var (e, s) = Params(_signer.SegmentUrl("chan1", seg, TimeSpan.FromMinutes(10)));

        var result = await _controller.Segment("chan1", seg, e, s, default);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(200, _controller.Response.StatusCode);
        Assert.Equal("video/mp2t", _controller.Response.ContentType);
        Assert.Equal(new byte[] { 0x47, 9, 9 }, _body.ToArray());
        Assert.Equal(new Uri(seg), _upstream.Requests.Single().Uri);
        Assert.Contains("sign2=", _upstream.Requests.Single().Header("Content-Auth"));
    }

    [Fact]
    public async Task Segment_url_that_was_not_signed_is_forbidden()
    {
        var (e, s) = Params(_signer.SegmentUrl("chan1", "http://seg.test/a.ts", TimeSpan.FromMinutes(10)));

        var result = await _controller.Segment("chan1", "http://internal.test/admin", e, s, default);

        Assert.Equal(403, Status(result));
        Assert.Empty(_upstream.Requests);
    }

    [Theory]
    [InlineData(null, "http://x.test/a.ts")]
    [InlineData("chan1", null)]
    [InlineData("", "")]
    public async Task Segment_missing_parameters_are_forbidden(string? channel, string? url)
    {
        Assert.Equal(403, Status(await _controller.Segment(channel, url, 1, "aa", default)));
    }

    [Fact]
    public async Task Segment_upstream_error_status_is_relayed()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        const string seg = "http://seg.test/a.ts";
        var (e, s) = Params(_signer.SegmentUrl("chan1", seg, TimeSpan.FromMinutes(10)));

        await _controller.Segment("chan1", seg, e, s, default);

        Assert.Equal(404, _controller.Response.StatusCode);
    }

    // ---- VOD ----

    [Fact]
    public async Task Vod_range_request_relays_206_with_range_headers()
    {
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[10]) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            r.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 9, 5000);
            r.Headers.AcceptRanges.Add("bytes");
            return r;
        };
        _controller.Request.Headers.Range = "bytes=0-9";
        var (e, s) = Params(_signer.VodUrl("CONTENT1", string.Empty, TimeSpan.FromHours(1)));

        await _controller.Vod("CONTENT1", null, e, s, default);

        Assert.Equal("bytes=0-9", _upstream.Requests.Single().Header("Range"));
        Assert.Equal(206, _controller.Response.StatusCode);
        Assert.Equal("video/mp4", _controller.Response.ContentType);
        Assert.Equal(10, _controller.Response.ContentLength);
        Assert.Equal("bytes 0-9/5000", _controller.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", _controller.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(10, _body.Length);
    }

    [Fact]
    public async Task Vod_head_request_sends_no_body()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
        _controller.Request.Method = "HEAD";
        var (e, s) = Params(_signer.VodUrl("CONTENT1", string.Empty, TimeSpan.FromHours(1)));

        await _controller.Vod("CONTENT1", null, e, s, default);

        Assert.Equal(HttpMethod.Head, _upstream.Requests.Single().Method);
        Assert.Equal(200, _controller.Response.StatusCode);
        Assert.Equal(0, _body.Length);
    }

    [Fact]
    public async Task Vod_series_is_part_of_the_signature_and_reaches_the_resolver()
    {
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1]) };
        var (e, s) = Params(_signer.VodUrl("CONTENT1", "SERIES1", TimeSpan.FromHours(1)));

        Assert.Equal(403, Status(await _controller.Vod("CONTENT1", "OTHER", e, s, default)));
        Assert.Equal(403, Status(await _controller.Vod("CONTENT1", null, e, s, default)));

        await _controller.Vod("CONTENT1", "SERIES1", e, s, default);
        Assert.Equal("SERIES1", _resolver.LastVodSeries);
    }

    [Fact]
    public async Task Vod_with_a_bad_signature_is_forbidden()
    {
        Assert.Equal(403, Status(await _controller.Vod("CONTENT1", null, 1, "aa", default)));
        Assert.Empty(_upstream.Requests);
    }

    // ---- images ----

    [Fact]
    public async Task Image_normalizes_the_cdns_nonstandard_image_jpg_content_type_and_streams_the_body()
    {
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpg");
            return r;
        };
        const string poster = "https://cdn.test/poster.jpg";
        var (e, s) = Params(_signer.ImageUrl(poster, TimeSpan.FromDays(1)));

        var result = await _controller.Image(poster, e, s, default);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(200, _controller.Response.StatusCode);
        // Jellyfin's own image cache/converter throws on "image/jpg" ("Unable to determine image file extension
        // from mime type image/jpg") -- this is the whole point of routing posters through this endpoint.
        Assert.Equal("image/jpeg", _controller.Response.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, _body.ToArray());
        Assert.Equal(new Uri(poster), _upstream.Requests.Single().Uri);
    }

    [Fact]
    public async Task Image_passes_through_an_already_standard_content_type_unchanged()
    {
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return r;
        };
        const string poster = "https://cdn.test/poster.png";
        var (e, s) = Params(_signer.ImageUrl(poster, TimeSpan.FromDays(1)));

        await _controller.Image(poster, e, s, default);

        Assert.Equal("image/png", _controller.Response.ContentType);
    }

    [Fact]
    public async Task Image_with_a_vague_image_star_type_is_served_under_its_real_type()
    {
        // Measured 2026-09-24: some portal posters come back as "image/*", which Jellyfin can't save.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        _upstream.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/*");
            return r;
        };
        const string poster = "https://cdn.test/poster";
        var (e, s) = Params(_signer.ImageUrl(poster, TimeSpan.FromDays(1)));

        await _controller.Image(poster, e, s, default);

        Assert.Equal("image/png", _controller.Response.ContentType);
        Assert.Equal(png.Length, _controller.Response.ContentLength);
        Assert.Equal(png, _body.ToArray());
    }

    [Fact]
    public async Task Image_with_a_bad_signature_is_forbidden_and_never_hits_upstream()
    {
        var result = await _controller.Image("https://cdn.test/poster.jpg", _clock.Now.AddHours(1).ToUnixTimeSeconds(), "deadbeef", default);

        Assert.Equal(403, Status(result));
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Image_url_that_was_not_signed_is_forbidden()
    {
        var (e, s) = Params(_signer.ImageUrl("https://cdn.test/poster.jpg", TimeSpan.FromDays(1)));

        var result = await _controller.Image("https://cdn.test/other.jpg", e, s, default);

        Assert.Equal(403, Status(result));
        Assert.Empty(_upstream.Requests);
    }
}
