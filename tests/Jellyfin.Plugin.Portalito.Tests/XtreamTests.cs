using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Configuration;
using Jellyfin.Plugin.Portalito.Xtream;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>The IPTV (Xtream) secondary source: title normalization, JSON coercion, and the match indexes.</summary>
public class XtreamTests
{
    [Theory]
    [InlineData("Amélie (2001)", "amelie")]
    [InlineData("The Matrix", "the matrix")]
    [InlineData("Spider-Man: No Way Home", "spider man no way home")]
    [InlineData("WALL·E", "wall e")]
    [InlineData("  Extra   spaces  (1999) ", "extra spaces")]
    [InlineData("Coração Selvagem", "coracao selvagem")]
    public void NormalizeTitle_strips_year_accents_and_punctuation(string input, string expected)
        => Assert.Equal(expected, XtreamCatalog.NormalizeTitle(input));

    [Fact]
    public void NormalizeTitle_only_drops_a_trailing_year_not_a_leading_one()
        => Assert.Equal("2001 a space odyssey", XtreamCatalog.NormalizeTitle("2001: A Space Odyssey"));

    [Theory]
    [InlineData("2026-10-13", null, 2026)]
    [InlineData(null, "Street Fighter (2026)", 2026)]
    [InlineData("", "No year here", null)]
    [InlineData("bad-date", "Also none", null)]
    public void YearOf_reads_an_iso_date_or_a_parenthesized_year(string? date, string? name, int? expected)
        => Assert.Equal(expected, XtreamClient.YearOf(date, name));

    [Theory]
    [InlineData("\"1153576\"", 1153576)]
    [InlineData("42", 42)]
    [InlineData("\"not a number\"", null)]
    [InlineData("true", null)]
    public void Int_coerces_string_or_number(string json, int? expected)
        => Assert.Equal(expected, XtreamClient.Int(JsonNode.Parse(json)));

    [Fact]
    public void Int_of_null_is_null() => Assert.Null(XtreamClient.Int(null));

    [Theory]
    [InlineData("\"Active\"", "Active")]
    [InlineData("1153576", "1153576")]
    public void Str_reads_string_or_number_the_same(string json, string expected)
        => Assert.Equal(expected, XtreamClient.Str(JsonNode.Parse(json)));

    [Fact]
    public void BuildSeriesIndexes_maps_tmdb_to_series_id_and_skips_missing()
    {
        var index = XtreamCatalog.BuildSeriesIndexes(new[]
        {
            new XtreamSeriesItem(10418, "Secret Service (2026)", 240411, "28", 2026),
            new XtreamSeriesItem(99, "No TMDB", null, "28", null),
            new XtreamSeriesItem(100, "Zero TMDB", 0, "28", null),
            new XtreamSeriesItem(777, "Duplicate upload", 240411, "28", 2026),
        }).ByTmdb;

        Assert.Single(index);
        Assert.Equal(10418, index[240411]); // first id wins; the duplicate (777) is ignored
        Assert.False(index.ContainsKey(0));
    }

    [Fact]
    public void BuildSeriesIndexes_also_matches_by_name_for_episode_playback()
    {
        var byName = XtreamCatalog.BuildSeriesIndexes(new[]
        {
            new XtreamSeriesItem(10418, "Secret Service (2026)", 240411, "28", 2026),
            new XtreamSeriesItem(11, "Dune: Prophecy (2024)", null, "28", 2024),
            new XtreamSeriesItem(12, "Dune: Prophecy (2026)", null, "28", 2026),
        }).ByName;

        Assert.Equal(10418, XtreamCatalog.MatchTitle(byName, "Secret Service", null));          // unique title, no year
        Assert.Equal(12, XtreamCatalog.MatchTitle(byName, "Dune: Prophecy", 2026));             // year disambiguates
        Assert.Null(XtreamCatalog.MatchTitle(byName, "Dune: Prophecy", null));                  // two candidates -> no guess
        Assert.Null(XtreamCatalog.MatchTitle(byName, "Unknown Show", 2020));
    }

    [Fact]
    public void MatchTitle_matches_on_title_and_year()
    {
        var index = XtreamCatalog.BuildMovieIndex(new[]
        {
            new XtreamVodItem(821207, "Street Fighter (2026)", "mp4", "258"),
            new XtreamVodItem(500, "The Matrix (1999)", "mkv", "1"),
        });

        var match = XtreamCatalog.MatchTitle(index, "Street Fighter", 2026);
        Assert.NotNull(match);
        Assert.Equal(821207, match!.StreamId);
        Assert.Equal("mp4", match.ContainerExtension);
    }

    [Fact]
    public void MatchTitle_falls_back_to_a_unique_title_when_year_is_unknown()
    {
        var index = XtreamCatalog.BuildMovieIndex(new[] { new XtreamVodItem(500, "The Matrix (1999)", "mkv", "1") });

        var match = XtreamCatalog.MatchTitle(index, "the matrix", year: null);
        Assert.Equal(500, match!.StreamId);
    }

    [Fact]
    public void MatchTitle_is_ambiguous_without_a_year_when_a_title_repeats()
    {
        var index = XtreamCatalog.BuildMovieIndex(new[]
        {
            new XtreamVodItem(1, "Dune (1984)", "mp4", "1"),
            new XtreamVodItem(2, "Dune (2021)", "mp4", "1"),
        });

        Assert.Null(XtreamCatalog.MatchTitle(index, "Dune", year: null));        // two candidates -> no guess
        Assert.Equal(2, XtreamCatalog.MatchTitle(index, "Dune", 2021)!.StreamId); // the year disambiguates
    }

    [Fact]
    public void MatchTitle_returns_null_when_nothing_matches()
    {
        var index = XtreamCatalog.BuildMovieIndex(new[] { new XtreamVodItem(1, "Dune (2021)", "mp4", "1") });
        Assert.Null(XtreamCatalog.MatchTitle(index, "Nonexistent Film", 2020));
    }

    [Fact]
    public void XtreamOptions_is_configured_only_when_all_three_fields_are_set()
    {
        var config = new PluginConfiguration { XtreamBaseUrl = "http://host:8080/", XtreamUsername = "u", XtreamPassword = " p " };
        var options = XtreamOptions.FromConfiguration(config);

        Assert.True(options.IsConfigured);
        Assert.Equal("http://host:8080", options.BaseUrl); // trailing slash trimmed
        Assert.Equal(" p ", options.Password);             // password kept verbatim (may hold spaces)

        Assert.False(XtreamOptions.FromConfiguration(new PluginConfiguration { XtreamBaseUrl = "http://host" }).IsConfigured);
    }

    [Theory]
    [InlineData("", PlaybackSourceMode.PortalOnly)]
    [InlineData("PortalOnly", PlaybackSourceMode.PortalOnly)]
    [InlineData("iptvonly", PlaybackSourceMode.IptvOnly)]
    [InlineData("IptvThenPortal", PlaybackSourceMode.IptvThenPortal)]
    [InlineData("nonsense", PlaybackSourceMode.PortalOnly)]
    public void PlaybackSourceMode_parses_or_defaults(string stored, PlaybackSourceMode expected)
        => Assert.Equal(expected, new PluginConfiguration { PlaybackSource = stored }.PlaybackSourceMode());
}
