using Jellyfin.Plugin.PortalitoTV.Content;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

public class PortalitoLibraryTests
{
    [Theory]
    [InlineData("/config/data/portalito/siguiendo/Show [portalito-AB12]/Season 1/S01E01.strm", "Show [portalito-AB12]/Season 1/S01E01.strm")]
    [InlineData("/mnt/media/anywhere/else/Show [portalito-ab_12-x]/Season 2/S02E10.strm", "Show [portalito-ab_12-x]/Season 2/S02E10.strm")]
    [InlineData("C:\\data\\follow\\Show [portalito-AB12]\\Season 1\\S01E01.strm", "Show [portalito-AB12]\\Season 1\\S01E01.strm")]
    [InlineData("Show [portalito-AB12]/Season 1/S01E01.strm", "Show [portalito-AB12]/Season 1/S01E01.strm")]
    public void RelativeKeyStartsAtTheShowFolderWhateverTheLibraryPath(string path, string expected)
    {
        Assert.True(PortalitoLibrary.TryRelativeKey(path, out var key));
        Assert.Equal(expected, key);
        Assert.Equal(expected, PortalitoLibrary.RelativeKey(path));
    }

    [Theory]
    [InlineData("/media/movies/Some Movie (2020)/movie.mkv")]
    [InlineData("/config/data/portalito/siguiendo/portalito-siguiendo.txt")]
    [InlineData("")]
    public void RelativeKeyLeavesOtherPathsAlone(string path)
    {
        Assert.False(PortalitoLibrary.TryRelativeKey(path, out var key));
        Assert.Equal(path, key);
    }

    [Fact]
    public void ProbeKeyOfAnEpisodeIsItsPathFromTheShowFolder()
    {
        var episode = new Episode { Path = "/custom/follow/dir/Show [portalito-AB12]/Season 3/S03E04.strm" };
        Assert.Equal("Show [portalito-AB12]/Season 3/S03E04.strm", InputResolver.ProbeKey(episode));
    }

    [Fact]
    public void ProbeKeyOfAMovieIsItsId()
    {
        var movie = new Movie { Id = Guid.Parse("11111111-2222-3333-4444-555555555555"), Path = "/media/movies/film.strm" };
        Assert.Equal("movie:11111111222233334444555555555555", InputResolver.ProbeKey(movie));
    }

    [Fact]
    public void SiguiendoSeriesAreRecognizedByTheirTagEvenOutsideTheDefaultFolder()
    {
        var tagged = new Series { Path = "/somewhere/Show", Tags = new[] { "other", "Portalito-Siguiendo" } };
        Assert.True(PortalitoLibrary.IsSiguiendo(tagged));
    }

    [Fact]
    public void SiguiendoSeriesAreRecognizedByTheirFolderTagToo()
    {
        var byFolder = new Series { Path = "/somewhere/Show [portalito-AB12]", Tags = Array.Empty<string>() };
        Assert.True(PortalitoLibrary.IsSiguiendo(byFolder));
    }

    [Fact]
    public void OtherSeriesAreNotSiguiendo()
    {
        var other = new Series { Path = "/media/shows/Show", Tags = new[] { "comedy" } };
        Assert.False(PortalitoLibrary.IsSiguiendo(other));
    }

    [Fact]
    public void PortalitoIdComesFromTheProviderIdOrTheFolder()
    {
        var provided = new Series { Path = "/x/Show" };
        provided.SetProviderId("portalito", "ab12");
        Assert.Equal("AB12", PortalitoLibrary.PortalitoIdOf(provided));

        var folder = new Series { Path = "/x/Show [portalito-cd34]" };
        Assert.Equal("CD34", PortalitoLibrary.PortalitoIdOf(folder));

        Assert.Null(PortalitoLibrary.PortalitoIdOf(new Series { Path = "/x/Show" }));
    }

    [Theory]
    [InlineData("Some Show - Español Latino", "Some Show")]
    [InlineData("Some Show - Espanol Latino", "Some Show")]
    [InlineData("Some Show", "Some Show")]
    [InlineData("Latino Show - Part", "Latino Show - Part")]
    public void DisplayNameDropsTheLatinoSuffix(string name, string expected) => Assert.Equal(expected, PortalitoLibrary.DisplayName(name));
}
