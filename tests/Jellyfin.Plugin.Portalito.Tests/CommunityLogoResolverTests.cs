using Jellyfin.Plugin.Portalito.Metadata;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// The community IPTV logo lookup: joining the channel list and the logo list, the decorated-name normalization, and
/// strict matching. Synthetic data only.
/// </summary>
public class CommunityLogoResolverTests
{
    private const string Channels = """
    [
      { "id": "A24.ar", "name": "A24", "alt_names": ["Canal A24"] },
      { "id": "Volver.ar", "name": "Volver", "alt_names": [] },
      { "id": "Canal13.cl", "name": "Canal 13", "alt_names": [] },
      { "id": "NoLogo.ar", "name": "No Logo Channel", "alt_names": [] }
    ]
    """;

    private const string Logos = """
    [
      { "channel": "A24.ar", "url": "https://cdn.test/a24-small.png", "in_use": false, "width": 100, "height": 100 },
      { "channel": "A24.ar", "url": "https://cdn.test/a24-big.png", "in_use": true, "width": 500, "height": 500 },
      { "channel": "Volver.ar", "url": "https://cdn.test/volver.png", "in_use": true, "width": 300, "height": 300 },
      { "channel": "Canal13.cl", "url": "https://cdn.test/canal13.png", "in_use": true, "width": 200, "height": 200 },
      { "channel": "Ghost.ar", "url": "ftp://cdn.test/x.png" }
    ]
    """;

    [Theory]
    [InlineData("AR | A24  ʜᴅ", "a24")]           // country prefix + small-caps quality suffix dropped
    [InlineData("AR l VOLVER HD", "volver")]       // "l" separator + HD dropped
    [InlineData("CL | C∆N∆L 13 HD ¹", "canal 13")] // ∆ -> a, superscript dropped, channel number kept
    [InlineData("America TV", "america tv")]        // nothing to strip
    [InlineData("AR I| PAKAPAKA HD", "pakapaka")]
    public void NormalizeChannelName_strips_prefixes_quality_and_look_alikes(string input, string expected)
        => Assert.Equal(expected, CommunityLogoResolver.NormalizeChannelName(input));

    [Fact]
    public void BuildIndex_joins_channels_to_logos_prefers_in_use_and_largest_and_skips_the_logoless()
    {
        var index = CommunityLogoResolver.BuildIndex(Channels, Logos);

        Assert.Equal("https://cdn.test/a24-big.png", index["a24"]);       // in-use + largest wins over the small one
        Assert.Equal("https://cdn.test/a24-big.png", index["canal a24"]); // via alt_names
        Assert.Equal("https://cdn.test/volver.png", index["volver"]);
        Assert.Equal("https://cdn.test/canal13.png", index["canal 13"]);
        Assert.DoesNotContain("no logo channel", index.Keys);            // channel has no logo entry
    }

    [Fact]
    public async Task FindLogoAsync_matches_decorated_names_and_returns_null_for_the_unknown()
    {
        var transport = new FakeTransport(new[] { Ok(Channels) }, new[] { Ok(Logos) });
        var resolver = new CommunityLogoResolver(transport, TimeProvider.System);

        Assert.Equal("https://cdn.test/a24-big.png", await resolver.FindLogoAsync("AR | A24  ʜᴅ", default));
        Assert.Equal("https://cdn.test/volver.png", await resolver.FindLogoAsync("AR l VOLVER HD", default));
        Assert.Null(await resolver.FindLogoAsync("24/7 Some Movie Loop", default));

        // Both files fetched once; the built index is reused.
        Assert.Equal(1, transport.ChannelCalls);
        Assert.Equal(1, transport.LogoCalls);
    }

    [Fact]
    public async Task FindLogoAsync_does_not_cache_a_transient_failure()
    {
        var transport = new FakeTransport(
            new[] { Ok(Channels), Ok(Channels) },
            new Func<string?>[] { () => throw new HttpRequestException("boom"), Ok(Logos) });
        var resolver = new CommunityLogoResolver(transport, TimeProvider.System);

        await Assert.ThrowsAsync<HttpRequestException>(() => resolver.FindLogoAsync("A24", default));
        Assert.Equal("https://cdn.test/a24-big.png", await resolver.FindLogoAsync("A24", default)); // retried, now works
    }

    [Fact]
    public async Task FindLogoAsync_treats_a_missing_file_as_an_empty_set()
    {
        var transport = new FakeTransport(new Func<string?>[] { () => null }, new[] { Ok(Logos) });
        var resolver = new CommunityLogoResolver(transport, TimeProvider.System);

        Assert.Null(await resolver.FindLogoAsync("A24", default));
    }

    private static Func<string?> Ok(string body) => () => body;

    private sealed class FakeTransport : ICommunityLogoTransport
    {
        private readonly Queue<Func<string?>> _channels;
        private readonly Queue<Func<string?>> _logos;

        public FakeTransport(Func<string?>[] channels, Func<string?>[] logos)
        {
            _channels = new Queue<Func<string?>>(channels);
            _logos = new Queue<Func<string?>>(logos);
        }

        public int ChannelCalls { get; private set; }

        public int LogoCalls { get; private set; }

        public async Task<string?> GetChannelsJsonAsync(CancellationToken cancellationToken)
        {
            ChannelCalls++;
            await Task.Yield();
            return _channels.Dequeue()();
        }

        public async Task<string?> GetLogosJsonAsync(CancellationToken cancellationToken)
        {
            LogoCalls++;
            await Task.Yield();
            return _logos.Dequeue()();
        }
    }
}
