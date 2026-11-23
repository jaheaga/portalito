using Jellyfin.Plugin.Portalito.Metadata;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>The community IPTV logo set lookup: building the name index and strict normalized matching. Synthetic data only.</summary>
public class CommunityLogoResolverTests
{
    private const string Json = """
    [
      { "name": "Caracol Televisión", "alt_names": ["Caracol TV", "Canal Caracol"], "logo": "https://cdn.test/caracol.png" },
      { "name": "RCN", "alt_names": [], "logo": "https://cdn.test/rcn.svg" },
      { "name": "No Logo Here", "alt_names": [], "logo": "" },
      { "name": "Bad Scheme", "logo": "ftp://cdn.test/x.png" }
    ]
    """;

    [Fact]
    public void BuildIndex_maps_name_and_alt_names_normalized_and_skips_unusable_logos()
    {
        var index = CommunityLogoResolver.BuildIndex(Json);

        Assert.Equal("https://cdn.test/caracol.png", index["caracol television"]); // accents stripped, lowercased
        Assert.Equal("https://cdn.test/caracol.png", index["caracol tv"]);          // an alt name
        Assert.Equal("https://cdn.test/caracol.png", index["canal caracol"]);       // another alt name
        Assert.Equal("https://cdn.test/rcn.svg", index["rcn"]);
        Assert.DoesNotContain("no logo here", index.Keys);  // empty logo skipped
        Assert.DoesNotContain("bad scheme", index.Keys);    // non-http(s) logo skipped
    }

    [Fact]
    public async Task FindLogoAsync_matches_on_name_or_alt_name_and_returns_null_for_the_unknown()
    {
        var transport = new FakeTransport(() => Json);
        var resolver = new CommunityLogoResolver(transport, TimeProvider.System);

        Assert.Equal("https://cdn.test/caracol.png", await resolver.FindLogoAsync("Caracol TV", default));
        Assert.Equal("https://cdn.test/rcn.svg", await resolver.FindLogoAsync("rcn", default));
        Assert.Null(await resolver.FindLogoAsync("Some Random Channel", default));
        Assert.Null(await resolver.FindLogoAsync("   ", default));

        // The database is fetched once and the index reused.
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task FindLogoAsync_does_not_cache_a_transient_failure()
    {
        // First fetch throws (an outage); it must not be cached as "no logos" -- the next call retries and succeeds.
        var transport = new FakeTransport(
            () => throw new HttpRequestException("boom"),
            () => Json);
        var resolver = new CommunityLogoResolver(transport, TimeProvider.System);

        await Assert.ThrowsAsync<HttpRequestException>(() => resolver.FindLogoAsync("RCN", default));
        Assert.Equal("https://cdn.test/rcn.svg", await resolver.FindLogoAsync("RCN", default));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task FindLogoAsync_treats_a_404_as_an_empty_set()
    {
        var transport = new FakeTransport(() => null); // 404 -> null body
        var resolver = new CommunityLogoResolver(transport, TimeProvider.System);

        Assert.Null(await resolver.FindLogoAsync("RCN", default));
    }

    private sealed class FakeTransport : ICommunityLogoTransport
    {
        private readonly Queue<Func<string?>> _responses;

        public FakeTransport(params Func<string?>[] responses) => _responses = new Queue<Func<string?>>(responses);

        public int Calls { get; private set; }

        public async Task<string?> GetChannelsJsonAsync(CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            return _responses.Dequeue()(); // a throwing func becomes a faulted task
        }
    }
}
