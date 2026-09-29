using System.Net;
using System.Text.Encodings.Web;
using Jellyfin.Plugin.Portalito.Api;
using Jellyfin.Plugin.Portalito.Portal;
using Jellyfin.Plugin.Portalito.Proxy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// Runs <see cref="PortalitoProxyController"/> behind a real Kestrel + MVC pipeline on a loopback port, the way Jellyfin hosts
/// it. This is what the direct-invocation controller tests cannot prove: the route templates, DI resolution of the
/// controller, model binding of the signed query parameters, and that <c>[AllowAnonymous]</c> lets ffmpeg through a host
/// whose default policy is "authenticated users only". Only the upstream CDN is faked; nothing leaves the machine.
/// </summary>
public class KestrelPipelineTests : IAsyncLifetime
{
    private const string Token = "00112233445566778899aabbccddeeff";

    private readonly ManualClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000));
    private readonly FakeUpstream _upstream = new();
    private readonly FakeResolver _resolver = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private ProxyUrlSigner _signer = null!;
    private Func<PortalitoServices>? _servicesOverride;

    /// <summary>Kestrel's own MinResponseDataRate feature state after the most recent request, captured by a
    /// middleware that runs the controller action first (via <c>next()</c>) then reads it -- "feature-missing" if
    /// this test host somehow has no such feature, "disabled" if the action set MinDataRate to null, "enabled"
    /// (the untouched default) otherwise.</summary>
    private string? _minDataRateState;

    public async Task InitializeAsync()
    {
        _resolver.Live = _ => new LiveStream("chan1", "cfl.test", "base&token=" + Token, "LIC", Token, null);
        _resolver.Vod = _ => new VodStream("MEDIA1", "vod.test", "vod-auth", "LIC-vod", null);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IPortalitoServicesProvider>(new LateBoundProvider(() => (_servicesOverride ?? DefaultServices)()));
        builder.Services.AddControllers().AddApplicationPart(typeof(PortalitoProxyController).Assembly);

        // Deny by default, like a Jellyfin install: anything not marked [AllowAnonymous] needs an authenticated user, and
        // this scheme never authenticates anyone.
        builder.Services.AddAuthentication("never").AddScheme<AuthenticationSchemeOptions, NeverAuthenticates>("never", null);
        builder.Services.AddAuthorization(o =>
        {
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

            // Jellyfin's admin-only policy, by the name the admin controller asks for.
            o.AddPolicy("RequiresElevation", p => p.RequireAuthenticatedUser().RequireRole("Administrator"));
        });

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.Use(async (context, next) =>
        {
            await next();
            var feature = context.Features.Get<IHttpMinResponseDataRateFeature>();
            _minDataRateState = feature is null ? "feature-missing" : feature.MinDataRate is null ? "disabled" : "enabled";
        });
        _app.MapControllers();
        _app.MapGet("/protected", () => "secret");
        await _app.StartAsync();

        var baseUrl = _app.Urls.Single().TrimEnd('/');
        _signer = new ProxyUrlSigner("unit-test-secret", baseUrl, _clock);
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl + "/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private PortalitoServices DefaultServices()
    {
        var proxy = new PortalitoProxyService(new HttpClient(_upstream), _resolver, _signer, TestContentAuth.Signer(), new ProxyIdentity("app", "1", "ua"), _clock);
        return new PortalitoServices(null!, _signer, proxy, null!);
    }

    private sealed class LateBoundProvider : IPortalitoServicesProvider
    {
        private readonly Func<PortalitoServices> _get;

        public LateBoundProvider(Func<PortalitoServices> get) => _get = get;

        public PortalitoServices Get() => _get();
    }

    private sealed class NeverAuthenticates : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public NeverAuthenticates(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private static HttpResponseMessage Bytes(HttpStatusCode status, byte[] body) => new(status) { Content = new ByteArrayContent(body) };

    // ---- the harness itself ----

    [Fact]
    public async Task Harness_denies_anonymous_requests_to_routes_that_are_not_allow_anonymous()
    {
        // If this were 200 the [AllowAnonymous] assertions below would prove nothing.
        var response = await _client.GetAsync("protected");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- config page endpoints ----

    [Fact]
    public async Task Ping_answers_its_signed_nonce_anonymously_so_the_connection_test_can_prove_the_proxy_url()
    {
        var response = await _client.GetAsync(_signer.PingUrl("n0nce", TimeSpan.FromMinutes(1)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("n0nce", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ping_with_a_forged_signature_is_forbidden()
    {
        var response = await _client.GetAsync("Portalito/ping?n=n0nce&e=9999999999&s=00");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_connection_test_is_not_reachable_anonymously()
    {
        // It signs in to the portal with the saved account: admins only, never [AllowAnonymous] like the proxy.
        var response = await _client.PostAsync("Portalito/Admin/Test", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- live ----

    [Fact]
    public async Task Live_playlist_then_segment_round_trip_over_http()
    {
        _upstream.Respond = r => r.Uri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:4,\nhttp://seg.test/live/chan1/a b.ts?x=1&y=2\n") }
            : Bytes(HttpStatusCode.OK, new byte[] { 0x47, 1, 2, 3 });

        var playlist = await _client.GetAsync(_signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));

        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", playlist.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", playlist.Headers.CacheControl?.ToString());
        var lines = (await playlist.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("http://cfl.test/live/chan1.m3u8", _upstream.Requests.Single().Uri.ToString());
        var segmentUrl = lines.Single(l => !l.StartsWith('#'));
        Assert.StartsWith(_client.BaseAddress + "Portalito/seg.ts?", segmentUrl);

        // ffmpeg follows the rewritten URL as-is; the signature must survive the query-string round trip.
        var segment = await _client.GetAsync(segmentUrl);

        Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
        Assert.Equal("video/mp2t", segment.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0x47, 1, 2, 3 }, await segment.Content.ReadAsByteArrayAsync());
        var upstreamSegment = _upstream.Requests.Last();
        Assert.Equal("http://seg.test/live/chan1/a%20b.ts?x=1&y=2", upstreamSegment.Uri.AbsoluteUri);
        Assert.Contains("sign2=", upstreamSegment.Header("Content-Auth"));
    }

    [Theory]
    [InlineData("chan1")]
    [InlineData("cx_50fdcc0817d61_720p")] // a representative channel-code shape
    [InlineData("CH_9-hd")]
    public async Task Live_route_template_binds_the_channel_code_before_the_m3u8_suffix(string channel)
    {
        // The CDN is asked for the resolved stream's name, so the fake resolves the bound channel as itself.
        _resolver.Live = _ => new LiveStream(channel, "cfl.test", "base&token=" + Token, "LIC", Token, null);
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n") };

        var response = await _client.GetAsync(_signer.LivePlaylistUrl(channel, TimeSpan.FromHours(1)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"http://cfl.test/live/{channel}.m3u8", _upstream.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task Live_channel_codes_outside_the_safe_charset_are_a_400_even_when_correctly_signed()
    {
        // The route still binds "chan.with.dots" (the template splits on the last ".m3u8"); the proxy then refuses ids that
        // could alter the upstream path. Real codes never contain dots.
        var response = await _client.GetAsync(_signer.LivePlaylistUrl("chan.with.dots", TimeSpan.FromHours(1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Live_playlist_with_a_tampered_signature_is_403_and_never_reaches_upstream()
    {
        var url = _signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1));
        var tampered = url[..^1] + (url[^1] == '0' ? '1' : '0');

        var response = await _client.GetAsync(tampered);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Live_playlist_for_another_channel_cannot_reuse_a_signature()
    {
        var url = _signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1));

        var response = await _client.GetAsync(url.Replace("/live/chan1.m3u8", "/live/chan2.m3u8", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Live_playlist_without_query_parameters_is_403()
    {
        var response = await _client.GetAsync("Portalito/live/chan1.m3u8");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Segment_url_for_a_different_upstream_is_403_over_http()
    {
        var url = _signer.SegmentUrl("chan1", "http://seg.test/a.ts", TimeSpan.FromMinutes(10));
        var forged = url.Replace(Uri.EscapeDataString("http://seg.test/a.ts"), Uri.EscapeDataString("http://internal.test/admin"), StringComparison.Ordinal);
        Assert.NotEqual(url, forged);

        var response = await _client.GetAsync(forged);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Expired_signature_is_403_over_http()
    {
        var url = _signer.LivePlaylistUrl("chan1", TimeSpan.FromMinutes(1));
        _clock.Now = _clock.Now.AddMinutes(2);

        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- VOD ----

    [Fact]
    public async Task Vod_range_request_is_forwarded_and_relayed_as_206_over_http()
    {
        _upstream.Respond = _ =>
        {
            var r = Bytes(HttpStatusCode.PartialContent, new byte[10]);
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            r.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 9, 5000);
            r.Headers.AcceptRanges.Add("bytes");
            return r;
        };
        var request = new HttpRequestMessage(HttpMethod.Get, _signer.VodUrl("CONTENT1", string.Empty, TimeSpan.FromHours(1)));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes=0-9", _upstream.Requests.Single().Header("Range"));
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes 0-9/5000", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(10, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Vod_head_is_routed_and_returns_headers_without_a_body()
    {
        _upstream.Respond = _ => Bytes(HttpStatusCode.OK, new byte[10]);
        var request = new HttpRequestMessage(HttpMethod.Head, _signer.VodUrl("CONTENT1", "SERIES1", TimeSpan.FromHours(1)));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Head, _upstream.Requests.Single().Method);
        Assert.Equal(10, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("SERIES1", _resolver.LastVodSeries);
    }

    [Fact]
    public async Task Vod_only_serves_get_and_head()
    {
        var response = await _client.PostAsync(_signer.VodUrl("CONTENT1", string.Empty, TimeSpan.FromHours(1)), new ByteArrayContent(Array.Empty<byte>()));

        // 405 from routing, or 401 from this harness's deny-by-default fallback policy (the 405 endpoint carries no
        // [AllowAnonymous]); either way it is refused and the upstream is never contacted.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.Unauthorized });
        Assert.Empty(_upstream.Requests);
    }

    // ---- Kestrel's minimum response data rate ----
    //
    // Measured live 2026-09-21: Kestrel's default MinResponseDataRate (240 B/s, enforced per write, not averaged,
    // after a 5s grace period) silently truncated VOD playback -- ffmpeg (transcoding, not just relaying) reads far
    // slower and burstier than that. RelayAsync disables it for exactly the two routes that stream a long-lived
    // upstream body; the live playlist route (a single small in-memory string via Content(), never RelayAsync) has
    // nothing to protect and is left alone as a contrast case.

    [Fact]
    public async Task Vod_relay_disables_kestrels_minimum_response_data_rate()
    {
        _upstream.Respond = _ => Bytes(HttpStatusCode.OK, new byte[] { 1, 2, 3 });

        await _client.GetAsync(_signer.VodUrl("CONTENT1", string.Empty, TimeSpan.FromHours(1)));

        Assert.Equal("disabled", _minDataRateState);
    }

    [Fact]
    public async Task Segment_relay_disables_kestrels_minimum_response_data_rate()
    {
        _upstream.Respond = r => r.Uri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:4,\nhttp://seg.test/a.ts\n") }
            : Bytes(HttpStatusCode.OK, new byte[] { 0x47, 1, 2, 3 });

        var playlist = await _client.GetAsync(_signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));
        var segmentUrl = (await playlist.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(l => !l.StartsWith('#'));

        await _client.GetAsync(segmentUrl);

        Assert.Equal("disabled", _minDataRateState);
    }

    [Fact]
    public async Task The_live_playlist_route_leaves_kestrels_minimum_response_data_rate_untouched()
    {
        // It answers with Content() (a single already-in-memory string), never RelayAsync -- no long-lived body
        // copy exists here for a slow reader to stall, so there is nothing for this fix to need to protect.
        _upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n") };

        await _client.GetAsync(_signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));

        Assert.Equal("enabled", _minDataRateState);
    }

    // ---- failure mapping over the wire ----

    [Fact]
    public async Task Unconfigured_plugin_is_503_over_http()
    {
        _servicesOverride = () => throw new PortalException("The 3DES key is not configured.");

        var response = await _client.GetAsync("Portalito/live/chan1.m3u8?e=1&s=aa");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Upstream_connection_failure_is_502_over_http()
    {
        _upstream.Respond = _ => throw new HttpRequestException("refused");

        var response = await _client.GetAsync(_signer.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
