using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Portal;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>The SLB discovery crypto/parse helpers and the per-request signature header. Synthetic keys and hosts only.</summary>
public class SlbDiscoveryTests
{
    private const string TestKey = "0123456789abcdef"; // 16-byte synthetic AES key

    [Fact]
    public void EncryptPayload_round_trips_and_prepends_a_random_iv()
    {
        const string plain = "{\"hello\":\"world\"}";

        var a = SlbDiscovery.EncryptPayload(plain, TestKey);
        var b = SlbDiscovery.EncryptPayload(plain, TestKey);

        Assert.NotEqual(a, b); // fresh random IV each time
        Assert.Equal(plain, SlbDiscovery.DecryptPayload(a, TestKey));
        Assert.Equal(plain, SlbDiscovery.DecryptPayload(b, TestKey));
    }

    [Fact]
    public void ParsePortalHosts_takes_the_named_service_group_before_global_and_dedupes()
    {
        var services = new JsonObject
        {
            ["global"] = new JsonArray
            {
                Entry("portal", "g1.example", "shared.example"),
                Entry("dcs", "ignored.example"),
            },
            ["group"] = new JsonArray
            {
                Entry("portal", "a1.example", "shared.example"),
            },
        };
        var response = Wrap(services);

        var hosts = SlbDiscovery.ParsePortalHosts(response, TestKey, "portal");

        // group first (a1), then global's new one (g1); shared.example appears once.
        Assert.Equal(new[] { "a1.example", "shared.example", "g1.example" }, hosts);
    }

    [Fact]
    public void ParsePortalHosts_returns_empty_when_the_service_is_absent_or_log_is_missing()
    {
        var services = new JsonObject { ["global"] = new JsonArray { Entry("dcs", "x.example") } };
        Assert.Empty(SlbDiscovery.ParsePortalHosts(Wrap(services), TestKey, "portal"));
        Assert.Empty(SlbDiscovery.ParsePortalHosts("{\"returnCode\":\"1\"}", TestKey, "portal"));
    }

    [Fact]
    public void SignatureHeaders_are_absent_without_a_secret_and_are_md5_of_nonce_plus_secret_with_one()
    {
        Assert.Equal((null, null), HttpPortalTransport.SignatureHeaders(string.Empty, "nonce"));

        var (trace, meta) = HttpPortalTransport.SignatureHeaders("secret", "the-nonce");
        Assert.Equal("the-nonce", trace);
        var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("the-nonce" + "secret"))).ToLowerInvariant();
        Assert.Equal(expected, meta);
        Assert.Matches("^[0-9a-f]{32}$", meta);
    }

    [Fact]
    public void ParsePortalHosts_strips_scheme_and_path_from_discovered_urls()
    {
        var services = new JsonObject
        {
            ["global"] = new JsonArray { Entry("portal", "https://g1.example/api/core", "g2.example:8443/x") },
        };

        var hosts = SlbDiscovery.ParsePortalHosts(Wrap(services), TestKey, "portal");

        Assert.Equal(new[] { "g1.example", "g2.example:8443" }, hosts);
    }

    [Fact]
    public async Task ResolvePortalHostsAsync_with_a_bad_length_key_returns_empty_without_throwing()
    {
        var options = new Jellyfin.Plugin.Portalito.Portal.PortalOptions
        {
            Hosts = new[] { "seed.example" },
            DiscoveryAesKey = "0123456789abcdefghij", // 20 bytes: not a valid AES key size (16/24/32)
            DiscoveryPath = "/v1/x/y",
            PortalUserAgent = "ua",
        };
        var discovery = new SlbDiscovery(new HttpClient(), options);

        // Degrades to "no discovered hosts" instead of throwing a CryptographicException out of every portal call.
        Assert.Empty(await discovery.ResolvePortalHostsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResolvePortalHostsAsync_records_the_seed_service_hosts_when_persistence_is_on()
    {
        var handler = new SeedHandler(Directory(), "seed.example");
        List<string>? saved = null;
        var discovery = new SlbDiscovery(new HttpClient(handler), SeedOptions(persist: true), s => saved = s.ToList());

        var hosts = await discovery.ResolvePortalHostsAsync(CancellationToken.None);

        // Returns the portal service's hosts (group before global) as before...
        Assert.Equal(new[] { "ftmrmy.test", "eajmnp.test" }, hosts);
        // ...and records the dcs service's hosts as the refreshed bootstrap seeds.
        Assert.Equal(new[] { "sydrgt.test", "ogvkxy.test", "fwmlba.test" }, saved);
    }

    [Fact]
    public async Task ResolvePortalHostsAsync_does_not_record_seeds_when_persistence_is_off()
    {
        var handler = new SeedHandler(Directory(), "seed.example");
        var called = false;
        var discovery = new SlbDiscovery(new HttpClient(handler), SeedOptions(persist: false), _ => called = true);

        var hosts = await discovery.ResolvePortalHostsAsync(CancellationToken.None);

        Assert.Equal(new[] { "ftmrmy.test", "eajmnp.test" }, hosts);
        Assert.False(called);
    }

    [Fact]
    public async Task ResolvePortalHostsAsync_does_not_rewrite_seeds_that_are_already_current()
    {
        var handler = new SeedHandler(Directory(), "seed.example");
        var called = false;
        var discovery = new SlbDiscovery(
            new HttpClient(handler),
            SeedOptions(persist: true, seeds: new[] { "sydrgt.test", "ogvkxy.test", "fwmlba.test" }),
            _ => called = true);

        await discovery.ResolvePortalHostsAsync(CancellationToken.None);

        Assert.False(called);
    }

    [Fact]
    public async Task ResolvePortalHostsAsync_bootstraps_from_a_learned_seed_when_the_configured_hosts_fail()
    {
        // The configured host is dead; a previously learned seed answers, so discovery still resolves.
        var handler = new SeedHandler(Directory(), "live.example");
        var discovery = new SlbDiscovery(
            new HttpClient(handler),
            SeedOptions(persist: false, hosts: new[] { "dead.example" }, seeds: new[] { "live.example" }));

        var hosts = await discovery.ResolvePortalHostsAsync(CancellationToken.None);

        Assert.Equal(new[] { "ftmrmy.test", "eajmnp.test" }, hosts);
        Assert.Contains("live.example", handler.Requested);
    }

    [Fact]
    public async Task ResolvePortalHostsAsync_keeps_fresh_seeds_first_and_caps_the_list()
    {
        var existing = Enumerable.Range(0, 7).Select(i => $"e{i}.test").ToArray();
        var handler = new SeedHandler(Directory(), "seed.example");
        List<string>? saved = null;
        var discovery = new SlbDiscovery(
            new HttpClient(handler),
            SeedOptions(persist: true, seeds: existing),
            s => saved = s.ToList());

        await discovery.ResolvePortalHostsAsync(CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(SlbDiscovery.MaxPersistedSeeds, saved!.Count);
        Assert.Equal(new[] { "sydrgt.test", "ogvkxy.test", "fwmlba.test" }, saved.Take(3)); // fresh first
        Assert.Equal(saved.Count, saved.Distinct(StringComparer.Ordinal).Count());          // no duplicates
    }

    private static PortalOptions SeedOptions(bool persist, string[]? hosts = null, string[]? seeds = null) => new()
    {
        Hosts = hosts ?? new[] { "seed.example" },
        DiscoveryAesKey = TestKey,
        DiscoveryPath = "/v1/x/y",
        DiscoveryService = "portal",
        DiscoverySeedService = "dcs",
        PersistDiscoveredSeeds = persist,
        DiscoverySeeds = seeds ?? Array.Empty<string>(),
        PortalUserAgent = "ua",
    };

    // A directory carrying both the portal API hosts and the dcs (bootstrap-seed) hosts, group before global.
    private static string Directory() => Wrap(new JsonObject
    {
        ["group"] = new JsonArray { Entry("portal", "ftmrmy.test"), Entry("dcs", "sydrgt.test") },
        ["global"] = new JsonArray { Entry("portal", "eajmnp.test"), Entry("dcs", "ogvkxy.test", "fwmlba.test") },
    });

    private sealed class SeedHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HashSet<string> _liveHosts;

        public SeedHandler(string body, params string[] liveHosts)
        {
            _body = body;
            _liveHosts = new HashSet<string>(liveHosts, StringComparer.Ordinal);
        }

        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Requested.Add(host);
            var ok = _liveHosts.Contains(host);
            return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent(ok ? _body : string.Empty),
            });
        }
    }

    private static JsonObject Entry(string service, params string[] urls)
    {
        var details = new JsonArray();
        foreach (var url in urls)
        {
            details.Add(new JsonObject { ["host_alias"] = "x", ["url"] = url });
        }

        return new JsonObject { ["business_entry"] = service, ["details"] = details };
    }

    private static string Wrap(JsonObject services)
        => new JsonObject { ["log"] = SlbDiscovery.EncryptPayload(services.ToJsonString(), TestKey) }.ToJsonString();
}
