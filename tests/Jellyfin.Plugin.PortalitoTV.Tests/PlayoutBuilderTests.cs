using Jellyfin.Plugin.PortalitoTV.Configuration;
using Jellyfin.Plugin.PortalitoTV.Media;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

public class PlayoutBuilderTests
{
    // A fixed UTC-5 zone without daylight saving, the same on every platform.
    private static readonly TimeZoneInfo Utc5 = TimeZoneInfo.CreateCustomTimeZone("UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");

    /// <summary>Monday 2026-10-19 06:00 at UTC-5 = 11:00 UTC.</summary>
    private static readonly DateTime Monday0600Utc = new(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc);

    private sealed class FakeContent : IContentLookup
    {
        private readonly Dictionary<string, List<ContentEntry>> _sets = new();

        public FakeContent Series(string id, int count, TimeSpan duration, int seasons = 1)
        {
            var list = new List<ContentEntry>();
            for (int i = 0; i < count; i++)
            {
                int season = 1 + (i * seasons / count);
                int episode = i + 1;
                var itemId = Guid.NewGuid();
                var template = new PlayoutItem(itemId, DateTime.MinValue, DateTime.MinValue + duration, TimeSpan.Zero,
                    EmitLanguage.Auto, id, $"{id} {season}x{episode}", season, episode);
                list.Add(new ContentEntry(itemId, duration, season, episode, template));
            }

            _sets[PlayoutBuilder.SeriesKey(id)] = list;
            return this;
        }

        /// <summary>A hearted movie: it goes into "movies" and has its own "movie:&lt;id&gt;" collection.</summary>
        public FakeContent Movie(Guid id, string title, TimeSpan duration)
        {
            var template = new PlayoutItem(id, DateTime.MinValue, DateTime.MinValue + duration, TimeSpan.Zero,
                EmitLanguage.Auto, title, IsMovie: true);
            var entry = new ContentEntry(id, duration, 0, 0, template);
            _sets[PlayoutBuilder.MovieKey(id)] = new List<ContentEntry> { entry };
            if (!_sets.TryGetValue(PlayoutBuilder.MoviesKey, out var all))
            {
                _sets[PlayoutBuilder.MoviesKey] = all = new List<ContentEntry>();
            }

            all.Add(entry);
            return this;
        }

        public IReadOnlyList<ContentEntry> Collection(string key)
        {
            if (key.StartsWith("mix:", StringComparison.Ordinal))
            {
                return key[4..].Split('|').SelectMany(Collection).ToList();
            }

            return _sets.TryGetValue(key, out var l) ? l : new List<ContentEntry>();
        }
    }

    private static ChannelSettings Channel(params BlockSettings[] blocks) => new()
    {
        Number = "900",
        Mode = ChannelMode.Schedule,
        Blocks = blocks.ToList(),
        FillerSeries = new List<string> { "F" },
    };

    private static BlockSettings Block(string start, string series, BlockOrder order = BlockOrder.Shuffle, params int[] days) =>
        new() { Start = start, SeriesId = series, Order = order, Days = days.ToList() };

    private static void AssertContiguous(Playout p)
    {
        for (int i = 1; i < p.Items.Count; i++)
        {
            Assert.Equal(p.Items[i - 1].EndUtc, p.Items[i].StartUtc);
        }
    }

    [Fact]
    public void BlockFloodsWholeEpisodesThenCutsFillerSoNextBlockStartsOnTime()
    {
        var content = new FakeContent().Series("A", 20, TimeSpan.FromMinutes(22)).Series("B", 20, TimeSpan.FromMinutes(22))
            .Series("F", 20, TimeSpan.FromMinutes(22));
        var ch = Channel(Block("06:00", "A"), Block("08:00", "B"));
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content, new Random(1)).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(4));

        var block1 = p.Items.Where(i => i.StartUtc < Monday0600Utc.AddHours(2)).ToList();
        Assert.Equal(5, block1.Count(i => i.Title == "A" && !i.IsFiller)); // 5 x 22 = 110 min
        var filler = Assert.Single(block1, i => i.IsFiller);
        Assert.Equal(TimeSpan.FromMinutes(10), filler.Duration); // cut
        Assert.Contains(p.Items, i => i.Title == "B" && i.StartUtc == Monday0600Utc.AddHours(2)); // 08:00 sharp
        AssertContiguous(p);
    }

    [Fact]
    public void BlockWithEndTimeStopsThereAndFillerCoversUntilNextBlock()
    {
        var content = new FakeContent().Series("A", 20, TimeSpan.FromMinutes(22)).Series("B", 20, TimeSpan.FromMinutes(22))
            .Series("F", 20, TimeSpan.FromMinutes(22));
        var a = Block("06:00", "A");
        a.End = "07:00";
        var ch = Channel(a, Block("08:00", "B"));
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(2).AddMinutes(1));

        var aItems = p.Items.Where(i => i.Title == "A").ToList();
        Assert.Equal(2, aItems.Count); // 2 x 22 = 44 min; the 3rd would end at 07:06
        Assert.True(aItems[^1].EndUtc <= Monday0600Utc.AddHours(1));
        Assert.All(p.Items.Where(i => i.StartUtc >= aItems[^1].EndUtc && i.StartUtc < Monday0600Utc.AddHours(2)), i => Assert.True(i.IsFiller));
        Assert.Contains(p.Items, i => i.Title == "B" && i.StartUtc == Monday0600Utc.AddHours(2));
        AssertContiguous(p);
    }

    [Fact]
    public void BlockEndBeforeStartEndsNextDay()
    {
        var content = new FakeContent().Series("LATE", 10, TimeSpan.FromMinutes(62)).Series("F", 20, TimeSpan.FromMinutes(22));
        var late = Block("22:00", "LATE", BlockOrder.Chronological);
        late.End = "00:30";
        var ch = Channel(late, Block("06:00", "F"));
        var start = new DateTime(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc); // Monday 22:00 at UTC-5
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content).Extend(p, start, start.AddHours(3));

        Assert.Equal(2, p.Items.Count(i => i.Title == "LATE" && i.StartUtc < start.AddMinutes(150)));
        Assert.All(p.Items.Where(i => i.StartUtc >= start.AddMinutes(150) && i.StartUtc < start.AddHours(3)), i => Assert.True(i.IsFiller));
    }

    [Fact]
    public void SpecificMovieBlockPlaysThatMovieOnceAndFillerCoversTheRest()
    {
        var movie = Guid.NewGuid();
        var content = new FakeContent().Movie(movie, "Movie", TimeSpan.FromMinutes(100)).Movie(Guid.NewGuid(), "Other", TimeSpan.FromMinutes(90))
            .Series("F", 20, TimeSpan.FromMinutes(22)).Series("B", 5, TimeSpan.FromMinutes(22));
        var ch = Channel(Block("06:00", "movie:" + movie.ToString("N")), Block("10:00", "B"));
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(4).AddMinutes(1));

        // A 4 h block: the movie (100 min) only once; it doesn't repeat even if it would fit again.
        Assert.Single(p.Items, i => i.Title == "Movie");
        Assert.Equal(Monday0600Utc, p.Items[0].StartUtc);
        Assert.DoesNotContain(p.Items, i => i.Title == "Other");
        Assert.All(p.Items.Where(i => i.Title != "Movie" && i.StartUtc < Monday0600Utc.AddHours(4)), i => Assert.True(i.IsFiller));
        Assert.Contains(p.Items, i => i.Title == "B" && i.StartUtc == Monday0600Utc.AddHours(4));
        AssertContiguous(p);
    }

    [Fact]
    public void MovieThatDoesNotFitTheBlockLeavesItToFiller()
    {
        var movie = Guid.NewGuid();
        var content = new FakeContent().Movie(movie, "Long", TimeSpan.FromMinutes(130)).Series("F", 20, TimeSpan.FromMinutes(22)).Series("B", 5, TimeSpan.FromMinutes(22));
        var ch = Channel(Block("06:00", "movie:" + movie.ToString("N")), Block("08:00", "B"));
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(2));

        Assert.DoesNotContain(p.Items, i => i.Title == "Long");
        Assert.All(p.Items.Where(i => i.StartUtc < Monday0600Utc.AddHours(2)), i => Assert.True(i.IsFiller));
    }

    [Fact]
    public void MoviesPoolBlockPlaysDifferentMoviesEvenIfChronologicalIsAsked()
    {
        var content = new FakeContent().Movie(Guid.NewGuid(), "M1", TimeSpan.FromMinutes(60)).Movie(Guid.NewGuid(), "M2", TimeSpan.FromMinutes(60))
            .Series("F", 20, TimeSpan.FromMinutes(22));
        var ch = Channel(Block("06:00", "movies", BlockOrder.Chronological), Block("08:00", "F"));
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(2));

        var movies = p.Items.Where(i => i.IsMovie && !i.IsFiller).Select(i => i.Title).ToList();
        Assert.Equal(2, movies.Count);
        Assert.NotEqual(movies[0], movies[1]); // each movie once before repeating
    }

    [Fact]
    public void EpisodeLongerThanBlockLeavesBlockToFiller()
    {
        var content = new FakeContent().Series("LONG", 3, TimeSpan.FromMinutes(70)).Series("A", 5, TimeSpan.FromMinutes(22))
            .Series("F", 10, TimeSpan.FromMinutes(22));
        var ch = Channel(Block("06:00", "LONG"), Block("07:00", "A"));
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content).Extend(p, Monday0600Utc, Monday0600Utc.AddHours(1));

        var first = p.Items.Where(i => i.StartUtc < Monday0600Utc.AddHours(1)).ToList();
        Assert.All(first, i => Assert.True(i.IsFiller));
        Assert.Equal(Monday0600Utc.AddHours(1), first[^1].EndUtc);
    }

    [Fact]
    public void ChronologicalContinuesNextDayAndWraps()
    {
        var content = new FakeContent().Series("SERIAL", 4, TimeSpan.FromMinutes(45), seasons: 2).Series("F", 10, TimeSpan.FromMinutes(22));
        var ch = Channel(Block("20:00", "SERIAL", BlockOrder.Chronological), Block("21:00", "F"));
        var p = new Playout();
        var start = new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc); // Monday 20:00 at UTC-5
        new PlayoutBuilder(ch, Utc5, content).Extend(p, start, start.AddDays(5).AddHours(1));

        var episodes = p.Items.Where(i => i.Title == "SERIAL" && !i.IsFiller).Select(i => i.Episode).ToList();
        Assert.Equal(new int?[] { 1, 2, 3, 4, 1 }, episodes.Take(5)); // one episode a night, back to the start
    }

    [Fact]
    public void TruncateReturnsFutureItemsAndChronologicalResumesFromFirstRemoved()
    {
        var content = new FakeContent().Series("SERIAL", 10, TimeSpan.FromMinutes(20)).Series("F", 10, TimeSpan.FromMinutes(22));
        var ch = Channel(Block("06:00", "SERIAL", BlockOrder.Chronological));
        var p = new Playout();
        var builder = new PlayoutBuilder(ch, Utc5, content);
        builder.Extend(p, Monday0600Utc, Monday0600Utc.AddHours(3));

        var now = Monday0600Utc.AddMinutes(30); // during episode 2
        PlayoutBuilder.TruncateAfterCurrent(p, now);
        Assert.Equal(2, p.Items.Count);
        Assert.Equal(Monday0600Utc.AddMinutes(40), p.BuiltUntilUtc);

        builder.Extend(p, now, now.AddHours(2));
        Assert.Equal(3, p.Items[2].Episode); // resumes with 3, skipping none
        AssertContiguous(p);
    }

    [Fact]
    public void TruncatedShuffleItemsComeBackFirst()
    {
        var content = new FakeContent().Series("A", 30, TimeSpan.FromMinutes(22)).Series("F", 10, TimeSpan.FromMinutes(22));
        var ch = Channel(Block("06:00", "A"));
        var p = new Playout();
        var builder = new PlayoutBuilder(ch, Utc5, content, new Random(7));
        builder.Extend(p, Monday0600Utc, Monday0600Utc.AddHours(3));
        var removedFirst = p.Items[1].ItemId;

        PlayoutBuilder.TruncateAfterCurrent(p, Monday0600Utc.AddMinutes(5));
        builder.Extend(p, Monday0600Utc.AddMinutes(5), Monday0600Utc.AddHours(3));
        Assert.Equal(removedFirst, p.Items[1].ItemId);
    }

    [Fact]
    public void DaysFilterAppliesBlockOnlyOnThoseDays()
    {
        var content = new FakeContent().Series("A", 50, TimeSpan.FromMinutes(30)).Series("M", 50, TimeSpan.FromMinutes(30))
            .Series("F", 10, TimeSpan.FromMinutes(30));
        var ch = Channel(Block("06:00", "A"), Block("06:00", "M", BlockOrder.Shuffle, (int)DayOfWeek.Monday), Block("07:00", "F"));
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content).Extend(p, Monday0600Utc, Monday0600Utc.AddDays(2));

        var monday = p.Items.First(i => i.StartUtc == Monday0600Utc);
        var tuesday = p.Items.First(i => i.StartUtc == Monday0600Utc.AddDays(1));
        Assert.Equal("M", monday.Title);
        Assert.Equal("A", tuesday.Title);
    }

    [Fact]
    public void ShuffleModeIsContinuousWithoutImmediateRepeat()
    {
        var content = new FakeContent().Series("A", 3, TimeSpan.FromMinutes(100)).Series("B", 2, TimeSpan.FromMinutes(90));
        var ch = new ChannelSettings { Number = "901", Mode = ChannelMode.Shuffle, FillerSeries = new() { "A", "B" } };
        var p = new Playout();
        new PlayoutBuilder(ch, Utc5, content, new Random(3)).Extend(p, Monday0600Utc, Monday0600Utc.AddDays(2));

        AssertContiguous(p);
        Assert.True(p.BuiltUntilUtc >= Monday0600Utc.AddDays(2));
        for (int i = 1; i < p.Items.Count; i++)
        {
            Assert.NotEqual(p.Items[i - 1].ItemId, p.Items[i].ItemId);
        }
    }

    [Fact]
    public void FullDayScheduleHasNoGapsAndBlocksStartOnTime()
    {
        var content = new FakeContent()
            .Series("S", 300, TimeSpan.FromSeconds(1320 + 37)).Series("FU", 100, TimeSpan.FromSeconds(1290))
            .Series("DRAMA1", 60, TimeSpan.FromSeconds(2820), 5).Series("DRAMA2", 40, TimeSpan.FromSeconds(3700), 4)
            .Series("F", 50, TimeSpan.FromSeconds(1300));
        var ch = Channel(Block("06:00", "S"), Block("08:00", "FU"), Block("20:00", "DRAMA1", BlockOrder.Chronological),
            Block("22:00", "DRAMA2", BlockOrder.Chronological), Block("00:30", string.Empty));
        var p = new Playout();
        var start = Monday0600Utc.AddMinutes(17); // starts in the middle of a block
        new PlayoutBuilder(ch, Utc5, content).Extend(p, start, start.AddHours(48));

        AssertContiguous(p);
        Assert.Equal(start, p.Items[0].StartUtc);
        foreach (var at in new[] { "08:00", "20:00", "22:00", "00:30", "06:00" })
        {
            var t = TimeSpan.Parse(at, System.Globalization.CultureInfo.InvariantCulture);
            var localDay = new DateTime(2026, 10, 20) + t;
            var utc = TimeZoneInfo.ConvertTimeToUtc(localDay, Utc5);
            Assert.Contains(p.Items, i => i.StartUtc == utc);
        }

        // 22:00-00:30: two ~62 min episodes fit
        var lateStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 20, 22, 0, 0), Utc5);
        Assert.Equal(2, p.Items.Count(i => i.Title == "DRAMA2" && i.StartUtc >= lateStart && i.StartUtc < lateStart.AddMinutes(150)));
    }

    [Fact]
    public void BlockTimesAreLocalToTheConfiguredTimeZone()
    {
        var ch = Channel(Block("20:00", "A"));
        var (_, _, next) = new PlayoutBuilder(ch, Utc5, new FakeContent()).BlockAt(new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc), next); // 20:00 at -05 = 01:00 UTC
    }

    [Fact]
    public void AudioSampleFindsTheStretchWithDialogue()
    {
        // 60 s at 8 kHz, L = requested track, R = reference. Music is the same on both; "voice" only on L between 31 s and 46 s.
        const int rate = 8000;
        var pcm = new byte[60 * rate * 4];
        var rng = new Random(5);
        for (int f = 0; f < 60 * rate; f++)
        {
            short music = (short)(3000 * Math.Sin(f * 0.05));
            short voice = f / rate is >= 31 and < 46 ? (short)rng.Next(-4000, 4000) : (short)0;
            BitConverter.GetBytes((short)(music + voice)).CopyTo(pcm, f * 4);
            BitConverter.GetBytes(music).CopyTo(pcm, (f * 4) + 2);
        }

        Assert.Equal(31, Jellyfin.Plugin.PortalitoTV.Api.AudioSampler.BestWindow(pcm, hasReference: true));
    }

    [Fact]
    public void ChoosesLatinoOrEnglishWithSubtitles()
    {
        var twoSpanish = new List<AudioTrack> { new("spa", null), new("spa", null), new("eng", null) };
        Assert.Equal(0, TrackSelector.Choose(twoSpanish, EmitLanguage.Auto, true).AudioOrdinal);

        var withPortuguese = new List<AudioTrack> { new("por", null), new("spa", null), new("spa", null), new("eng", null) };
        var c = TrackSelector.Choose(withPortuguese, EmitLanguage.EnglishWithSubtitles, true);
        Assert.Equal(3, c.AudioOrdinal);
        Assert.True(c.BurnSubtitles);

        var movie = new List<AudioTrack> { new("eng", null), new("spa", "Latino") };
        Assert.Equal(1, TrackSelector.Choose(movie, EmitLanguage.Auto, true).AudioOrdinal);

        // Two unnamed "spa" tracks, the first one Castilian: the user picks the 2nd.
        var unnamed = new List<AudioTrack> { new("spa", null), new("spa", null), new("eng", null) };
        Assert.Equal(1, TrackSelector.Choose(unnamed, EmitLanguage.SpanishSecond, true).AudioOrdinal);
        Assert.Equal(0, TrackSelector.Choose(new List<AudioTrack> { new("spa", null) }, EmitLanguage.SpanishSecond, true).AudioOrdinal);

        var english = new List<AudioTrack> { new("eng", null) };
        var e = TrackSelector.Choose(english, EmitLanguage.Auto, true);
        Assert.Equal(EmitLanguage.EnglishWithSubtitles, e.Language);
        Assert.True(e.BurnSubtitles);
    }
}
