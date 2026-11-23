using Jellyfin.Plugin.Portalito.Catalog;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>The durable TV IPTV channel-logo backfill map: positive/negative records, change detection, and persistence.</summary>
public class ChannelLogoStoreTests
{
    private static readonly DateTime When = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Records_a_found_logo_and_reads_it_back()
    {
        var store = new ChannelLogoStore(null);

        Assert.True(store.Record("Caracol TV", "https://cdn.test/caracol.png", When));
        Assert.True(store.TryGet("Caracol TV", out var record));
        Assert.Equal("https://cdn.test/caracol.png", record.Url);
    }

    [Fact]
    public void Records_a_no_match_as_a_negative_so_it_is_not_looked_up_again()
    {
        var store = new ChannelLogoStore(null);

        Assert.True(store.Record("Obscure Channel", null, When));
        Assert.True(store.TryGet("Obscure Channel", out var record)); // known...
        Assert.Null(record.Url);                                      // ...but a remembered no-match
    }

    [Fact]
    public void Re_recording_the_same_value_reports_no_change()
    {
        var store = new ChannelLogoStore(null);

        Assert.True(store.Record("RCN", "https://cdn.test/rcn.svg", When));
        Assert.False(store.Record("RCN", "https://cdn.test/rcn.svg", When.AddDays(5))); // unchanged URL, no rewrite
        Assert.True(store.Record("RCN", "https://cdn.test/rcn2.svg", When.AddDays(6)));  // changed URL
    }

    [Fact]
    public void A_name_that_normalizes_the_same_shares_one_record()
    {
        var store = new ChannelLogoStore(null);
        store.Record("Caracol TV", "https://cdn.test/caracol.png", When);

        // Accents/case/extra spaces normalize to the same key.
        Assert.True(store.TryGet("  caracol   tv ", out var record));
        Assert.Equal("https://cdn.test/caracol.png", record.Url);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Survives_a_reload_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), "portalito-logo-test-" + Guid.NewGuid().ToString("N"), "channel-logos.json");
        try
        {
            var first = new ChannelLogoStore(path);
            first.Record("Caracol TV", "https://cdn.test/caracol.png", When);
            first.Record("Obscure Channel", null, When);
            first.Flush();

            var reloaded = new ChannelLogoStore(path);
            Assert.True(reloaded.TryGet("Caracol TV", out var hit));
            Assert.Equal("https://cdn.test/caracol.png", hit.Url);
            Assert.True(reloaded.TryGet("Obscure Channel", out var miss));
            Assert.Null(miss.Url);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path)!;
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
