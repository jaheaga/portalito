using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// What Jellyfin needs to keep playback positions (Continue Watching) and place episodes in a series: a runtime on
/// every movie/episode, and a season + show name on every episode.
/// </summary>
public sealed class ContinueWatchingTests : IDisposable
{
    private static readonly DateTime Learned = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly WiringHarness _h = new();
    private readonly FakeVodTrackProbe _probe = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("portalito-cw-").FullName;
    private readonly RuntimeStore _runtimes = new(null);
    private readonly PortalitoVodChannel _channel;

    public ContinueWatchingTests()
        => _channel = new PortalitoVodChannel(_h, _probe, new SubtitleFileCache(_dir, new HttpClient()), new Collage.CollageService(_dir), NullLogger<PortalitoVodChannel>.Instance, _runtimes);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static InternalChannelItemQuery Query(string folderId) => new() { FolderId = folderId };

    private static JsonObject Season(string seasonId, string name, params JsonObject[] episodes) => new()
    {
        ["assetData"] = new JsonObject
        {
            ["name"] = name,
            ["sameSeasonSeriesList"] = WiringHarness.Array(
                new JsonObject { ["contentId"] = "S1ID", ["seasonNumber"] = 1 },
                new JsonObject { ["contentId"] = "S2ID", ["seasonNumber"] = 2 }),
            ["simpleProgramList"] = WiringHarness.Array(episodes),
        },
    };

    [Theory]
    [InlineData("2700", 27_000_000_000L)]
    [InlineData("01:30:00", 54_000_000_000L)]
    [InlineData("45:00", 27_000_000_000L)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("abc", null)]
    public void Portal_durations_parse_as_seconds_or_clock_time(string value, long? ticks)
        => Assert.Equal(ticks, RuntimeStore.ParseDuration(value));

    [Fact]
    public void Runtimes_survive_a_restart_and_unchanged_values_are_not_rewritten()
    {
        var path = Path.Combine(_dir, "sub", "runtimes.json");
        var store = new RuntimeStore(path);

        Assert.True(store.Set("M1", 123, Learned));
        Assert.False(store.Set("M1", 123, Learned.AddDays(1))); // same runtime: no change, learned date kept
        store.Flush(); // saves are batched; a restart would come after the pending write
        Assert.True(new RuntimeStore(path).TryGet("M1", out var reloaded));
        Assert.Equal(new KnownRuntime(123, Learned), reloaded);
    }

    [Fact]
    public void A_damaged_runtimes_file_is_kept_aside_not_overwritten()
    {
        var path = Path.Combine(_dir, "bad", "runtimes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var store = new RuntimeStore(path);
        store.Set("M1", 123, Learned);
        store.Flush();

        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
        Assert.True(new RuntimeStore(path).TryGet("M1", out _));
    }

    [Fact]
    public void Many_runtimes_in_a_burst_are_written_once_batched()
    {
        var path = Path.Combine(_dir, "burst", "runtimes.json");
        var store = new RuntimeStore(path);

        for (var i = 0; i < 500; i++)
        {
            store.Set("T" + i, 1000 + i, Learned);
        }

        Assert.False(File.Exists(path)); // not yet: one write after the burst, not one per runtime
        store.Flush();
        Assert.Equal(500, new RuntimeStore(path).Count);
    }

    [Fact]
    public async Task Episodes_carry_their_season_show_name_and_known_runtime()
    {
        _runtimes.Set("EP1", 26_000_000_000, Learned);
        _h.Route = r => r.Path == "v4/getItemData"
            ? _h.Ok(Season("S2ID", "Alquimia de Almas T2",
                new JsonObject { ["contentId"] = "EP1", ["name"] = "Alquimia de Almas T2_01", ["seriesNumber"] = 1 },
                new JsonObject { ["contentId"] = "EP2", ["name"] = "Alquimia de Almas T2_02", ["seriesNumber"] = 2, ["duration"] = "1800" }))
            : FakePortalTransport.Error("x", r.Path);

        var items = (await _channel.GetChannelItems(Query("ssn:S2ID"), default)).Items;

        Assert.All(items, i =>
        {
            Assert.Equal(2, i.ParentIndexNumber);           // from the season's own entry, not a guess
            Assert.Equal("Alquimia de Almas", i.SeriesName); // the season name without "T2"
        });
        Assert.Equal(26_000_000_000, items[0].RunTimeTicks); // learned earlier (a playback probe)
        Assert.Equal(Learned, items[0].DateModified);        // newer DateModified = Jellyfin saves the change
        Assert.Equal(18_000_000_000, items[1].RunTimeTicks); // from the portal's own duration, now remembered
        Assert.True(_runtimes.TryGet("EP2", out _));
    }

    [Fact]
    public async Task A_season_missing_from_the_list_falls_back_to_its_name_marker()
    {
        _h.Route = r => r.Path == "v4/getItemData"
            ? _h.Ok(Season("S9ID", "Some Show T3", new JsonObject { ["contentId"] = "E1", ["seriesNumber"] = 1 }))
            : FakePortalTransport.Error("x", r.Path);

        var episode = Assert.Single((await _channel.GetChannelItems(Query("ssn:S9ID"), default)).Items);

        Assert.Equal(3, episode.ParentIndexNumber);
        Assert.Equal("Some Show", episode.SeriesName);
    }

    [Fact]
    public async Task Playing_a_title_records_its_probed_runtime_for_later_listings()
    {
        _probe.Respond = () => new ProbedTracks(new[] { new MediaStream { Type = MediaStreamType.Video, Index = 0 } }, RunTimeTicks: 55_000_000_000, Bitrate: null);
        _h.Route = PortalitoVodChannelTests.VodRoute(_h, "ts");

        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default));

        Assert.Equal(55_000_000_000, source.RunTimeTicks);
        Assert.True(_runtimes.TryGet("MOVIE1", out var known));
        Assert.Equal(55_000_000_000, known.Ticks);
    }

    [Fact]
    public async Task When_the_probe_fails_a_remembered_runtime_still_reaches_the_source()
    {
        _runtimes.Set("MOVIE1", 42_000_000_000, Learned);
        _probe.Respond = () => throw new IOException("probe died");
        _h.Route = PortalitoVodChannelTests.VodRoute(_h, "ts");

        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default));

        Assert.Equal(42_000_000_000, source.RunTimeTicks);
    }

    [Theory]
    [InlineData("epi:S2ID:E1", null, null, "Show T2_05", 2, 2)]       // parent season folder wins
    [InlineData("epi:S2ID:E1", null, null, "Show T2_05", null, 2)]    // else the name's marker, "_" read as a separator
    [InlineData("epi:S2ID:E1", null, 1, "Show T2_05", 2, null)]       // already has a season: untouched
    [InlineData("epi:S2ID:E1", null, 0, "Show", null, 1)]             // season 0 isn't a season: no marker -> 1
    [InlineData("mov:M1", null, null, "Movie", null, null)]           // movies have no season
    public void The_repair_fills_only_missing_episode_seasons(string externalId, long? runtime, int? season, string name, int? parentSeason, int? expected)
        => Assert.Equal(expected, SeriesRepair.Plan(externalId, runtime, season, name, parentSeason, new RuntimeStore(null)).ParentIndexNumber);

    [Fact]
    public void The_repair_fills_known_runtimes_and_ignores_foreign_items()
    {
        var store = new RuntimeStore(null);
        store.Set("E1", 30_000_000_000, Learned);
        store.Set("M1", 60_000_000_000, Learned);

        Assert.Equal(30_000_000_000, SeriesRepair.Plan("epi:S1ID:E1", null, 1, "x", 1, store).RunTimeTicks);
        Assert.Equal(60_000_000_000, SeriesRepair.Plan("mov:M1", 1L, null, "x", null, store).RunTimeTicks);
        Assert.True(SeriesRepair.Plan("mov:M1", 60_000_000_000, null, "x", null, store).IsEmpty);  // already right
        Assert.True(SeriesRepair.Plan("mov:UNKNOWN", null, null, "x", null, store).IsEmpty);       // nothing known
        Assert.True(SeriesRepair.Plan("shw:X", null, null, "x", null, store).IsEmpty);              // a folder
        Assert.True(SeriesRepair.Plan("something-else", null, null, "x", null, store).IsEmpty);     // not ours
    }
}
