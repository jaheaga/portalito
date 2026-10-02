using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Portal;
using Jellyfin.Plugin.Portalito.Proxy;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// A real <see cref="PortalClient"/> over the fake transport plus a real signer, exposed the way Jellyfin's DI
/// sees them (<see cref="IPortalitoServicesProvider"/>). Routes answer by portal path; activation is handled here.
/// Also carries a real <see cref="CatalogBrowser"/> over the SAME fake portal transport (it's a Portalito client too),
/// so "Descubrir" tests just add more routed paths, same as everything else.
/// </summary>
internal sealed class WiringHarness : IPortalitoServicesProvider
{
    public const string ProxyBase = "http://127.0.0.1:8096";

    private readonly Func<PortalitoServices> _get;

    /// <param name="featuredRows">Destacado rows (FeaturedRows syntax); needs <paramref name="withTmdb"/>.</param>
    public WiringHarness(bool withTmdb = false, string? featuredRows = null)
    {
        Clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000));
        Transport = new FakePortalTransport();
        Transport.Handler = r => r.Path == "v8/active"
            ? Transport.Ok(new JsonObject { ["userId"] = "u1", ["userToken"] = "tok1" })
            : Route(r);
        var options = new PortalOptions
        {
            TripleDesKeyHex = PortalCipherTests.TestKeyHex,
            Hosts = new[] { "host-a.test" },
            AppId = "com.example.app",
            ApkVersion = "12345",
            DeviceSn = "TESTSN0001",
            Portal = "portal1",
            ApiBasePath = "/api/core/",
            LoginPasswordSalt = "testsalt",
            LiveColumnCode = "live_all",
            AllChannelsColumnId = 999,
        };
        var portal = new PortalClient(options, Transport);
        Signer = new ProxyUrlSigner("unit-test-secret", ProxyBase, Clock);
        var proxy = new PortalitoProxyService(new HttpClient(), portal, Signer, TestContentAuth.Signer(), new ProxyIdentity("app", "1", "ua"), Clock);
        var catalogs = new CatalogBrowser(Clock, CatalogBrowser.ParseCatalogs("cat_movies:Películas,cat_series:Series,cat_kids:Infantil,cat_anime:Anime"));
        var tmdb = withTmdb ? new Metadata.TmdbClient(Tmdb, "test-key", Clock) : null;
        var rows = DiscoveryBrowser.ParseRows(featuredRows);
        var discovery = tmdb is not null && rows.Count > 0 ? new DiscoveryBrowser(tmdb, Clock, rows) : null;
        var services = new PortalitoServices(portal, Signer, proxy, catalogs, tmdb, Discovery: discovery);
        _get = () => services;
    }

    public ManualClock Clock { get; }

    public FakePortalTransport Transport { get; }

    public ProxyUrlSigner Signer { get; }

    /// <summary>Gets the fake TMDB behind the harness's TMDB client (only used when built with <c>withTmdb</c>).</summary>
    public FakeTmdbTransport Tmdb { get; } = new();

    /// <summary>Gets or sets the portal routes (everything except activation).</summary>
    public Func<FakeRequest, string> Route { get; set; } = r => FakePortalTransport.Error("noroute", r.Path);

    public PortalitoServices Get() => _get();

    public string Ok(JsonObject payload) => Transport.Ok(payload);

    public static JsonArray Array(params JsonNode[] items) => new(items);
}

/// <summary>A track probe that answers with scripted tracks (none by default) and counts its calls.</summary>
internal sealed class FakeVodTrackProbe : Channels.IVodTrackProbe
{
    public int Calls { get; private set; }

    public MediaBrowser.Model.Dto.MediaSourceInfo? LastSource { get; private set; }

    public Func<Channels.ProbedTracks> Respond { get; set; } = () => new Channels.ProbedTracks(System.Array.Empty<MediaBrowser.Model.Entities.MediaStream>(), null, null);

    public Task<Channels.ProbedTracks> ProbeAsync(MediaBrowser.Model.Dto.MediaSourceInfo source, CancellationToken cancellationToken)
    {
        Calls++;
        LastSource = source;
        return Task.FromResult(Respond());
    }
}

/// <summary>An <see cref="IPortalitoServicesProvider"/> that fails like an unconfigured plugin.</summary>
internal sealed class UnconfiguredProvider : IPortalitoServicesProvider
{
    public PortalitoServices Get() => throw new PortalException("The 3DES key is not configured.");
}
