using Jellyfin.Plugin.PortalitoTV.Configuration;
using Jellyfin.Plugin.PortalitoTV.Content;
using Jellyfin.Plugin.PortalitoTV.Media;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

public class MultiPartTests
{
    // A fixed UTC-5 zone without daylight saving, the same on every platform.
    private static readonly TimeZoneInfo Utc5 = TimeZoneInfo.CreateCustomTimeZone("UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
    private static readonly DateTime Monday0600Utc = new(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc);

    private static ContentEntry E(int season, int episode, string? title, int minutes = 22, string? group = null, int part = 0, int count = 0)
    {
        var id = Guid.NewGuid();
        var d = TimeSpan.FromMinutes(minutes);
        var template = new PlayoutItem(id, DateTime.MinValue, DateTime.MinValue + d, TimeSpan.Zero, EmitLanguage.Auto, "S", title, season, episode,
            PartGroup: group, PartIndex: part, PartCount: count);
        return new ContentEntry(id, d, season, episode, template);
    }

    private static Dictionary<int, int> Last(params (int Season, int Episode)[] items) => items.ToDictionary(i => i.Season, i => i.Episode);

    // ---------- detection by name ----------

    [Theory]
    [InlineData("¿Quién fue? (primera parte)", 1, false)]
    [InlineData("¿Quién fue? (segunda parte)", 2, false)]
    [InlineData("Tercera parte de la trilogía \"La gran saga\"", 3, false)]
    [InlineData("La gran guerra (Parte 1)", 1, false)]
    [InlineData("Tierra lejana - Parte 2", 2, false)]
    [InlineData("Un título cualquiera (1)", 1, true)]
    [InlineData("Otro título distinto (2)", 2, true)]
    [InlineData("Something Big (Part Two)", 2, false)]
    [InlineData("The Finale (1/2)", 1, true)]
    public void ParsesPartMarkers(string title, int part, bool parenthesized)
    {
        var marker = MultiPart.Parse(title);
        Assert.NotNull(marker);
        Assert.Equal(part, marker!.Part);
        Assert.Equal(parenthesized, marker.Parenthesized);
    }

    [Theory]
    [InlineData("La ciudad que es parte del pueblo")]
    [InlineData("Antología de interés I")]
    [InlineData("Pedro IV")]
    [InlineData("Especial de otoño VII")]
    [InlineData("Un episodio cualquiera")]
    [InlineData("Part Time Job")]
    [InlineData("Episodio 5")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresTitlesWithoutRealPartMarkers(string? title) => Assert.Null(MultiPart.Parse(title));

    // ---------- grouping real episodes ----------

    [Fact]
    public void GroupsTwoPartsAcrossSeasons()
    {
        // The last episode of season 6 (25) + the first of season 7.
        var entries = new List<ContentEntry>
        {
            E(6, 24, "Otro episodio"), E(6, 25, "¿Quién fue? (primera parte)"),
            E(7, 1, "¿Quién fue? (segunda parte)"), E(7, 2, "Un episodio nuevo"),
        };
        var result = MultiPart.Assign(entries, Last((6, 25), (7, 25)));
        Assert.Null(result[0].Template.PartGroup);
        Assert.Equal((1, 2), (result[1].Template.PartIndex, result[1].Template.PartCount));
        Assert.Equal((2, 2), (result[2].Template.PartIndex, result[2].Template.PartCount));
        Assert.Equal(result[1].Template.PartGroup, result[2].Template.PartGroup);
        Assert.NotNull(result[1].Template.PartGroup);
        Assert.Null(result[3].Template.PartGroup);
    }

    [Fact]
    public void DoesNotJoinAcrossSeasonsWhenTheFirstIsNotTheLastEpisode()
    {
        var entries = new List<ContentEntry> { E(6, 20, "Algo (primera parte)"), E(7, 1, "Algo (segunda parte)") };
        var result = MultiPart.Assign(entries, Last((6, 25)));
        Assert.All(result, e => Assert.Null(e.Template.PartGroup));
    }

    [Fact]
    public void GroupsATrilogy()
    {
        const string t = " de la trilogía \"La gran saga\"";
        var entries = new List<ContentEntry>
        {
            E(4, 28, "Primera parte" + t), E(4, 29, "Segunda parte" + t), E(4, 30, "Tercera parte" + t),
        };
        var result = MultiPart.Assign(entries, Last((4, 30)));
        Assert.Equal(new[] { 1, 2, 3 }, result.Select(e => e.Template.PartIndex));
        Assert.All(result, e => Assert.Equal(3, e.Template.PartCount));
        Assert.Single(result.Select(e => e.Template.PartGroup).Distinct());
    }

    [Fact]
    public void GroupsPartsWithDifferentNamesWhenTheyUseParenthesizedNumbers()
    {
        var entries = new List<ContentEntry> { E(17, 1, "Un título cualquiera (1)"), E(17, 2, "Otro título distinto (2)") };
        var result = MultiPart.Assign(entries, Last((17, 20)));
        Assert.Equal(new[] { 1, 2 }, result.Select(e => e.Template.PartIndex));
    }

    [Fact]
    public void DifferentNamesWithWordMarkersAreNotJoined()
    {
        var entries = new List<ContentEntry> { E(1, 1, "Uno (Parte 1)"), E(1, 2, "Otro distinto (Parte 2)") };
        Assert.All(MultiPart.Assign(entries, Last((1, 10))), e => Assert.Null(e.Template.PartGroup));
    }

    [Fact]
    public void FirstPartWithoutMarkerIsRecognizedWhenItsTitleMatches()
    {
        var entries = new List<ContentEntry>
        {
            E(11, 9, "Otro"), E(11, 10, "Tierra lejana"), E(11, 11, "Tierra lejana - Parte 2"), E(11, 12, "Tierra lejana - Parte 3"), E(11, 13, "Siguiente"),
        };
        var result = MultiPart.Assign(entries, Last((11, 14)));
        Assert.Null(result[0].Template.PartGroup);
        Assert.Equal(new[] { 1, 2, 3 }, result.Skip(1).Take(3).Select(e => e.Template.PartIndex));
        Assert.All(result.Skip(1).Take(3), e => Assert.Equal(3, e.Template.PartCount));
        Assert.Null(result[4].Template.PartGroup);
    }

    [Fact]
    public void MissingOrNonConsecutivePartsStayLoose()
    {
        // Part 1 is missing (it isn't ready) so part 2 is left alone; another pair with a gap between them isn't joined either.
        var entries = new List<ContentEntry> { E(1, 5, "Cosa (segunda parte)"), E(1, 8, "Otra (primera parte)"), E(1, 10, "Otra (segunda parte)") };
        Assert.All(MultiPart.Assign(entries, Last((1, 12))), e => Assert.Null(e.Template.PartGroup));
    }

    [Fact]
    public void KeepsTheOriginalOrderOfTheList()
    {
        var entries = new List<ContentEntry> { E(2, 3, "Z (segunda parte)"), E(2, 2, "Z (primera parte)"), E(2, 1, "A") };
        var result = MultiPart.Assign(entries, Last((2, 10)));
        Assert.Equal(entries.Select(e => e.ItemId), result.Select(e => e.ItemId));
        Assert.Equal(2, result[0].Template.PartIndex);
        Assert.Equal(1, result[1].Template.PartIndex);
    }

    // ---------- scheduling ----------

    [Fact]
    public void ShuffleNeverSplitsOrReordersTheParts()
    {
        for (int seed = 0; seed < 40; seed++)
        {
            var content = new Lookup().Add(PlayoutBuilder.SeriesKey("S"), Series()).Add(PlayoutBuilder.SeriesKey("F"), E(9, 1, "Relleno", 22));
            var p = new Playout();
            new PlayoutBuilder(Channel("S", BlockOrder.Shuffle), Utc5, content, new Random(seed)).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(3).AddMinutes(1));

            for (int i = 0; i < p.Items.Count; i++)
            {
                var item = p.Items[i];
                if (item.PartIndex == 2)
                {
                    Assert.True(i > 0 && p.Items[i - 1].PartGroup == item.PartGroup && p.Items[i - 1].PartIndex == 1, $"seed {seed}: part 2 doesn't follow part 1");
                    Assert.Equal(p.Items[i - 1].EndUtc, item.StartUtc);
                }

                if (item.PartIndex == 1)
                {
                    Assert.True(i + 1 < p.Items.Count && p.Items[i + 1].PartIndex == 2, $"seed {seed}: part 1 was left without its part 2");
                }
            }
        }
    }

    [Fact]
    public void ShuffleStillPlaysTheMultipartEpisodeAndTheSingles()
    {
        var content = new Lookup().Add(PlayoutBuilder.SeriesKey("S"), Series()).Add(PlayoutBuilder.SeriesKey("F"), E(9, 1, "Relleno", 22));
        var p = new Playout();

        // Three days of 3 h blocks: every episode comes around several times.
        new PlayoutBuilder(Channel("S", BlockOrder.Shuffle), Utc5, content, new Random(7)).Extend(p, Monday0600Utc, Monday0600Utc.AddDays(3));
        var titles = p.Items.Where(i => !i.IsFiller).Select(i => i.EpisodeTitle).ToHashSet();
        Assert.Contains("Doble (primera parte)", titles);
        Assert.Contains("Doble (segunda parte)", titles);
        Assert.Contains("Suelto 1", titles);
    }

    [Fact]
    public void ChronologicalPlaysThePartsTogetherAndContinuesAfterThem()
    {
        var content = new Lookup().Add(PlayoutBuilder.SeriesKey("S"), Series()).Add(PlayoutBuilder.SeriesKey("F"), E(9, 1, "Relleno", 22));
        var p = new Playout();
        new PlayoutBuilder(Channel("S", BlockOrder.Chronological), Utc5, content, new Random(1)).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(3).AddMinutes(1));
        var order = p.Items.Where(i => !i.IsFiller && i.StartUtc < Monday0600Utc.AddHours(3)).Select(i => i.EpisodeTitle).ToList();

        // 6 episodes (22+22+24+24+22+22 = 136 min) fit in the 3 h block, in order, with both parts together.
        Assert.Equal(new[] { "Suelto 1", "Suelto 2", "Doble (primera parte)", "Doble (segunda parte)", "Suelto 3", "Suelto 4" }, order.Take(6));
        Assert.Equal(Monday0600Utc.AddMinutes(44), p.Items.First(i => i.PartIndex == 1).StartUtc);
        Assert.Equal(p.Items.First(i => i.PartIndex == 1).EndUtc, p.Items.First(i => i.PartIndex == 2).StartUtc);
    }

    [Fact]
    public void ChronologicalDoesNotStartAGroupThatDoesNotFitAndLeavesItForLater()
    {
        // A 60 min block: "Suelto 1" and "Suelto 2" fit (44 min) but the double one (48 min) doesn't -> the rest is filler and the double isn't cut.
        var content = new Lookup().Add(PlayoutBuilder.SeriesKey("S"), Series()).Add(PlayoutBuilder.SeriesKey("F"), E(9, 1, "Relleno", 22));
        var p = new Playout();
        new PlayoutBuilder(Channel("S", BlockOrder.Chronological, nextStart: "07:00"), Utc5, content, new Random(1))
            .Extend(p, Monday0600Utc, Monday0600Utc.AddHours(1));
        Assert.Equal(new[] { "Suelto 1", "Suelto 2" }, p.Items.Where(i => !i.IsFiller).Select(i => i.EpisodeTitle));
        Assert.DoesNotContain(p.Items, i => i.PartIndex > 0);
        Assert.All(p.Items.Where(i => i.StartUtc >= Monday0600Utc.AddMinutes(44)), i => Assert.True(i.IsFiller));
    }

    [Fact]
    public void ABlockTooShortForTheGroupPlaysOnlyFiller()
    {
        var group = new[] { E(1, 1, "Largo (primera parte)", 40, "1x1", 1, 2), E(1, 2, "Largo (segunda parte)", 40, "1x1", 2, 2) };
        var content = new Lookup().Add(PlayoutBuilder.SeriesKey("S"), group).Add(PlayoutBuilder.SeriesKey("F"), E(9, 1, "Relleno", 22));
        var p = new Playout();
        new PlayoutBuilder(Channel("S", BlockOrder.Shuffle, nextStart: "07:00"), Utc5, content, new Random(3)).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(1));
        Assert.All(p.Items, i => Assert.True(i.IsFiller));
    }

    [Fact]
    public void FillerNeverCarriesGroupData()
    {
        // Filler is cut: it uses the loose episodes (even if they are parts) and doesn't mark groups.
        var content = new Lookup().Add(PlayoutBuilder.SeriesKey("F"), Series()).Add(PlayoutBuilder.SeriesKey("S"), E(5, 1, "Algo", 30));
        var p = new Playout();
        new PlayoutBuilder(Channel("S", BlockOrder.Shuffle, nextStart: "07:00"), Utc5, content, new Random(2))
            .Extend(p, Monday0600Utc, Monday0600Utc.AddHours(2));
        Assert.All(p.Items.Where(i => i.IsFiller), i => Assert.Null(i.PartGroup));
    }

    [Fact]
    public void TruncateKeepsTheRemainingPartsOfAnEpisodeInProgress()
    {
        var p = new Playout();
        var start = Monday0600Utc;
        var group = "1x3";
        p.Items.Add(new PlayoutItem(Guid.NewGuid(), start, start.AddMinutes(24), TimeSpan.Zero, EmitLanguage.Auto, "S", "A (primera parte)", 1, 3,
            CollectionKey: "series:S", PartGroup: group, PartIndex: 1, PartCount: 2));
        p.Items.Add(new PlayoutItem(Guid.NewGuid(), start.AddMinutes(24), start.AddMinutes(48), TimeSpan.Zero, EmitLanguage.Auto, "S", "A (segunda parte)", 1, 4,
            CollectionKey: "series:S", PartGroup: group, PartIndex: 2, PartCount: 2));
        var after = Guid.NewGuid();
        p.Items.Add(new PlayoutItem(after, start.AddMinutes(48), start.AddMinutes(70), TimeSpan.Zero, EmitLanguage.Auto, "S", "B", 1, 5, CollectionKey: "series:S"));
        p.BuiltUntilUtc = start.AddMinutes(70);

        PlayoutBuilder.TruncateAfterCurrent(p, start.AddMinutes(10)); // part 1 is on air

        Assert.Equal(2, p.Items.Count); // part 2 is kept
        Assert.Equal(start.AddMinutes(48), p.BuiltUntilUtc);
        Assert.Equal(new[] { after }, p.State("series:S").Bag); // only the loose episode goes back; part 2 doesn't go back by itself
    }

    [Fact]
    public void TruncateReturnsOnlyTheFirstPartOfAGroupThatWasNotStarted()
    {
        var p = new Playout();
        var start = Monday0600Utc;
        p.Items.Add(new PlayoutItem(Guid.NewGuid(), start, start.AddMinutes(22), TimeSpan.Zero, EmitLanguage.Auto, "S", "X", 1, 1, CollectionKey: "series:S"));
        var first = Guid.NewGuid();
        p.Items.Add(new PlayoutItem(first, start.AddMinutes(22), start.AddMinutes(46), TimeSpan.Zero, EmitLanguage.Auto, "S", "A (primera parte)", 1, 3,
            CollectionKey: "series:S", PartGroup: "1x3", PartIndex: 1, PartCount: 2));
        p.Items.Add(new PlayoutItem(Guid.NewGuid(), start.AddMinutes(46), start.AddMinutes(70), TimeSpan.Zero, EmitLanguage.Auto, "S", "A (segunda parte)", 1, 4,
            CollectionKey: "series:S", PartGroup: "1x3", PartIndex: 2, PartCount: 2));

        PlayoutBuilder.TruncateAfterCurrent(p, start.AddMinutes(5)); // "X" is on air; the rest is dropped

        Assert.Single(p.Items);
        Assert.Equal(new[] { first }, p.State("series:S").Bag);
    }

    private static ChannelSettings Channel(string seriesId, BlockOrder order, string start = "06:00", string? nextStart = "09:00") => new()
    {
        Number = "900",
        Mode = ChannelMode.Schedule,
        FillerSeries = new List<string> { "F" },
        Blocks = new List<BlockSettings>
        {
            new() { Start = start, SeriesId = seriesId, Order = order },
            new() { Start = nextStart ?? "21:00", SeriesId = "F", Order = BlockOrder.Shuffle },
        },
    };

    private static ContentEntry[] Series() => new[]
    {
        E(1, 1, "Suelto 1"), E(1, 2, "Suelto 2"),
        E(1, 3, "Doble (primera parte)", 24, "1x3", 1, 2), E(1, 4, "Doble (segunda parte)", 24, "1x3", 2, 2),
        E(1, 5, "Suelto 3"), E(1, 6, "Suelto 4"),
    };

    private sealed class Lookup : IContentLookup
    {
        private readonly Dictionary<string, List<ContentEntry>> _sets = new();

        public Lookup Add(string key, params ContentEntry[] entries)
        {
            _sets[key] = entries.ToList();
            return this;
        }

        public IReadOnlyList<ContentEntry> Collection(string key) => _sets.TryGetValue(key, out var l) ? l : new List<ContentEntry>();
    }
}
