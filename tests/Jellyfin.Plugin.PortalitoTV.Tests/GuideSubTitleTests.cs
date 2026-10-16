using Jellyfin.Plugin.PortalitoTV.LiveTv;
using Jellyfin.Plugin.PortalitoTV.Media;
using Jellyfin.Plugin.PortalitoTV.Metadata;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

public class GuideSubTitleTests
{
    private static PlayoutItem Item(string? episodeTitle, int? season, int? episode, bool movie = false, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), DateTime.UnixEpoch, DateTime.UnixEpoch.AddMinutes(22), TimeSpan.Zero, EmitLanguage.Auto,
            "Show", episodeTitle, season, episode, IsMovie: movie);

    [Fact]
    public void EpisodeShowsSeasonEpisodeAndName() =>
        Assert.Equal("T3 E5 - Episode name", GuideController.SubTitle(Item("Episode name", 3, 5)));

    [Fact]
    public void NumbersAreNotPaddedAndNameIsTrimmed() =>
        Assert.Equal("T12 E104 - Final", GuideController.SubTitle(Item("  Final ", 12, 104)));

    [Fact]
    public void EpisodeWithoutNameShowsOnlyNumbers()
    {
        Assert.Equal("T1 E2", GuideController.SubTitle(Item(null, 1, 2)));
        Assert.Equal("T1 E2", GuideController.SubTitle(Item("  ", 1, 2)));
    }

    [Fact]
    public void WithoutNumbersKeepsTheEpisodeName()
    {
        Assert.Equal("Special", GuideController.SubTitle(Item("Special", null, null)));
        Assert.Equal("Special", GuideController.SubTitle(Item("Special", 1, null)));
        Assert.Null(GuideController.SubTitle(Item(null, null, null)));
    }

    [Theory]
    [InlineData("Episodio 3")]
    [InlineData("episode 12")]
    [InlineData("Capítulo 7")]
    [InlineData("Some Show Season 27 - 1")]
    [InlineData("Temporada 2 - 15")]
    public void PlaceholderNamesAreDroppedAndOnlyNumbersRemain(string placeholder)
    {
        Assert.Equal("T28 E4", GuideController.SubTitle(Item(placeholder, 28, 4)));
        Assert.Null(GuideController.SubTitle(Item(placeholder, null, null)));
    }

    [Theory]
    [InlineData("Episodio 3", true)]
    [InlineData("Some Show Season 27 - 1", true)]
    [InlineData("  ", true)]
    [InlineData(null, true)]
    [InlineData("A real name", false)]
    [InlineData("Season of the Witch", false)]
    [InlineData("Episodio final", false)]
    public void GenericTitleDetection(string? name, bool generic) =>
        Assert.Equal(generic, TmdbClient.IsGenericTitle(name));

    [Fact]
    public void MoviesAndSlateHaveNoSubTitle()
    {
        Assert.Null(GuideController.SubTitle(Item("Something", 1, 1, movie: true)));
        Assert.Null(GuideController.SubTitle(Item("Something", 1, 1, id: Guid.Empty)));
    }
}
