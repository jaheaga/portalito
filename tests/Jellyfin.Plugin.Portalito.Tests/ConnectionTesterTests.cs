using System.Net;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Api;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class ConnectionTesterTests
{
    private readonly WiringHarness _h = new();
    private readonly FakeUpstream _self = new();

    public ConnectionTesterTests()
    {
        _h.Route = r => r.Path == "v3/filterGenre" ? _h.Ok(new JsonObject { ["tags"] = new JsonArray() }) : FakePortalTransport.Error("x", r.Path);

        // This server's own ping endpoint, answering like the real controller: the nonce back iff the signature holds.
        _self.Respond = r =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(r.Uri.Query);
            return r.Uri.AbsolutePath == "/Portalito/ping" && _h.Signer.VerifyPing(q["n"]!, long.Parse(q["e"]!), q["s"])
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(q["n"]!) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    private sealed class Provider : IPortalitoServicesProvider
    {
        private readonly Func<PortalitoServices> _get;

        public Provider(Func<PortalitoServices> get) => _get = get;

        public PortalitoServices Get() => _get();
    }

    private Task<IReadOnlyList<ConnectionCheck>> Run(IPortalitoServicesProvider? provider = null)
        => new ConnectionTester(provider ?? _h, new HttpClient(_self)).RunAsync(default);

    private static ConnectionCheck Check(IReadOnlyList<ConnectionCheck> checks, string name) => Assert.Single(checks, c => c.Name == name);

    [Fact]
    public async Task A_working_setup_reports_every_check()
    {
        var checks = await Run();

        Assert.Equal(new[] { "Settings", "Portal", "Live TV", "Proxy URL", "Guide time zone" }, checks.Select(c => c.Name));
        Assert.Equal("ok", Check(checks, "Settings").Status);
        Assert.Equal("ok", Check(checks, "Portal").Status);
        Assert.Equal("ok", Check(checks, "Proxy URL").Status);
        Assert.Contains(WiringHarness.ProxyBase, Check(checks, "Proxy URL").Message);
        Assert.Equal("ok", Check(checks, "Guide time zone").Status);
    }

    [Fact]
    public async Task Without_an_account_live_tv_is_a_warning_not_an_error()
    {
        var live = Check(await Run(), "Live TV");

        Assert.Equal("warning", live.Status);
        Assert.Contains("needs a Portalito account", live.Message);
    }

    [Fact]
    public async Task Incomplete_settings_stop_at_the_first_check_with_the_reason()
    {
        var check = Assert.Single(await Run(new UnconfiguredProvider()));

        Assert.Equal("Settings", check.Name);
        Assert.Equal("error", check.Status);
        Assert.Contains("3DES key", check.Message);
    }

    [Fact]
    public async Task A_rejected_portal_certificate_points_at_the_tls_setting()
    {
        _h.Route = r => throw new HttpRequestException("The SSL connection could not be established, see inner exception.");

        var portal = Check(await Run(), "Portal");

        Assert.Equal("error", portal.Status);
        Assert.Contains("SSL connection", portal.Message);
        Assert.Contains("Skip portal TLS certificate verification", portal.Message);
    }

    [Fact]
    public async Task A_portal_error_is_reported_as_the_portal_put_it()
    {
        _h.Route = r => FakePortalTransport.Error("aaa100027", "session expired");

        var portal = Check(await Run(), "Portal");

        Assert.Equal("error", portal.Status);
        Assert.Contains("aaa100027", portal.Message);
    }

    [Fact]
    public async Task A_proxy_url_that_404s_says_how_to_fix_it()
    {
        _self.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var proxy = Check(await Run(), "Proxy URL");

        Assert.Equal("error", proxy.Status);
        Assert.Contains("404", proxy.Message);
        Assert.Contains("Proxy base URL", proxy.Message);
    }

    [Fact]
    public async Task A_proxy_url_answered_by_some_other_server_is_an_error()
    {
        _self.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>router login</html>") };

        var proxy = Check(await Run(), "Proxy URL");

        Assert.Equal("error", proxy.Status);
        Assert.Contains("not as this server's Portalito plugin", proxy.Message);
    }

    [Fact]
    public async Task An_unreachable_proxy_url_is_an_error()
    {
        _self.Respond = _ => throw new HttpRequestException("Connection refused");

        var proxy = Check(await Run(), "Proxy URL");

        Assert.Equal("error", proxy.Status);
        Assert.Contains("Connection refused", proxy.Message);
    }

    [Fact]
    public async Task An_unknown_guide_time_zone_is_a_warning()
    {
        var provider = new Provider(() => _h.Get() with { EpgTimeZoneProblem = "Time zone 'X' is unknown on this server" });

        var zone = Check(await Run(provider), "Guide time zone");

        Assert.Equal("warning", zone.Status);
        Assert.Contains("'X'", zone.Message);
    }
}
