using Jellyfin.Plugin.Portalito.Catalog;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class SeasonHandlingTests
{
    // Only these four forms are recognised (verbatim port of the reference client's own SEASON regex) -- no "Season"/
    // "Saison"/"Stagione" and no reversed "2a Temporada" form; those don't exist in the real source.
    [Theory]
    [InlineData("Show T5", 5)]
    [InlineData("Show Temp.2", 2)]
    [InlineData("Show Temp 2", 2)]
    [InlineData("Show Temporada 3", 3)]
    [InlineData("Show S04", 4)]
    public void Finds_the_season_number(string name, int expected)
        => Assert.Equal(expected, SeasonGrouping.SeasonFromName(name));

    [Fact]
    public void No_season_marker_defaults_to_season_one()
        => Assert.Equal(1, SeasonGrouping.SeasonFromName("Just A Movie"));

    [Fact]
    public void Blank_name_defaults_to_season_one()
        => Assert.Equal(1, SeasonGrouping.SeasonFromName(""));

    [Fact]
    public void Strips_the_season_marker_trims_and_lowercases()
        // WithoutSeason is a grouping key (never shown), so it lowercases -- matches the reference client exactly.
        => Assert.Equal("show", SeasonGrouping.WithoutSeason("Show Temporada 2"));

    [Fact]
    public void Without_season_on_a_title_with_no_marker_is_just_lowercased()
        => Assert.Equal("just a movie", SeasonGrouping.WithoutSeason("Just A Movie"));

    [Fact]
    public void StripSeasonForDisplay_keeps_the_original_case()
        => Assert.Equal("Show", SeasonGrouping.StripSeasonForDisplay("Show Temporada 2"));

    [Fact]
    public void StripSeasonForDisplay_on_a_title_with_no_marker_is_unchanged()
        => Assert.Equal("Just A Movie", SeasonGrouping.StripSeasonForDisplay("Just A Movie"));
}
