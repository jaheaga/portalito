using Jellyfin.Plugin.Portalito.Catalog;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class VodItemIdTests
{
    [Theory]
    [InlineData("liv:root")]
    [InlineData("lcat:76206")]
    [InlineData("lch:cx_7E95BA6378098331FD8BA_720p")]
    [InlineData("ssn:11A43548F9CC4E39A342CABD7D673AD9")]
    [InlineData("mov:11A43548F9CC4E39A342CABD7D673AD9")]
    [InlineData("epi:SERIES1:EPISODE_2")]
    [InlineData("fea:root")]
    [InlineData("row:0:estrenos")]
    [InlineData("row:3:top")]
    public void Round_trips_through_its_string_form(string text)
    {
        Assert.True(VodItemId.TryParse(text, out var id));
        Assert.Equal(text, id.ToString());
    }

    [Fact]
    public void Parses_a_curated_row_kind_and_parts()
    {
        Assert.True(VodItemId.TryParse("row:1:top", out var id));
        Assert.Equal(VodItemKind.Row, id.Kind);
        Assert.Equal("1", id.Primary);
        Assert.Equal("top", id.Secondary);
    }

    [Theory]
    [InlineData("row:x:top")]  // catalog index must be numeric
    [InlineData("row:0")]      // needs a mode
    [InlineData("row:0:a:b")]  // too many parts
    public void Rejects_malformed_row_ids(string text)
        => Assert.False(VodItemId.TryParse(text, out _));

    [Fact]
    public void Parses_the_kind_and_both_parts_of_an_episode()
    {
        Assert.True(VodItemId.TryParse("epi:S1:E9", out var id));
        Assert.Equal(VodItemKind.Episode, id.Kind);
        Assert.Equal("S1", id.Primary);
        Assert.Equal("E9", id.Secondary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mov")]
    [InlineData("mov:")]
    [InlineData("nope:123")]
    [InlineData("mov:a:b")]
    [InlineData("epi:onlyone")]
    [InlineData("epi:a:b:c")]
    [InlineData("mov:../etc")]
    [InlineData("mov:a/b")]
    [InlineData("mov:a b")]
    [InlineData("mov:a?x=1")]
    public void Rejects_malformed_or_unsafe_ids(string? text)
    {
        Assert.False(VodItemId.TryParse(text, out _));
    }

    [Fact]
    public void Rejects_over_long_parts()
    {
        Assert.False(VodItemId.TryParse("mov:" + new string('a', 65), out _));
        Assert.True(VodItemId.TryParse("mov:" + new string('a', 64), out _));
    }
}

public class PageWindowTests
{
    [Theory]
    [InlineData(0, 30, 30, 1, 1, 0)]
    [InlineData(30, 30, 30, 2, 2, 0)]
    [InlineData(15, 30, 30, 1, 2, 15)]
    [InlineData(0, 10, 30, 1, 1, 0)]
    [InlineData(25, 10, 30, 1, 2, 25)]
    [InlineData(60, 1, 30, 3, 3, 0)]
    public void Maps_a_window_onto_portal_pages(int start, int limit, int pageSize, int first, int last, int skip)
    {
        var w = new PageWindow(start, limit, pageSize);

        Assert.Equal(first, w.FirstPage);
        Assert.Equal(last, w.LastPage);
        Assert.Equal(skip, w.Skip);
    }

    [Fact]
    public void Defaults_to_the_first_full_page_and_ignores_nonsense()
    {
        Assert.Equal(new PageWindow(0, 30, 30), PageWindow.Create(null, null, 30));
        Assert.Equal(new PageWindow(0, 30, 30), PageWindow.Create(-5, 0, 30));
        Assert.Equal(new PageWindow(7, 4, 30), PageWindow.Create(7, 4, 30));
    }
}

public class LanguageCodesTests
{
    [Theory]
    [InlineData("es", "spa")]
    [InlineData("ES", "spa")]
    [InlineData("Latino", "spa")]
    [InlineData("español", "spa")]
    [InlineData(" en ", "eng")]
    [InlineData("ENGLISH", "eng")]
    [InlineData("pt-BR", "por")]
    [InlineData("spa", "spa")]
    public void Maps_portal_codes_and_names_to_iso_639_2(string portal, string expected)
        => Assert.Equal(expected, LanguageCodes.ToIso6392(portal));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("klingon")]
    public void Unknown_values_are_null(string? portal)
        => Assert.Null(LanguageCodes.ToIso6392(portal));
}

public class CatalogIndexWalkTests
{
    [Theory]
    [InlineData("dis:root")]
    [InlineData("cat:0")]
    [InlineData("flt:0:years")]
    [InlineData("flt:3:y2015")]
    [InlineData("liv:root")]
    [InlineData("lcat:76182")]
    [InlineData("lcat:76206")] // the index now walks every live category
    public void Opens_the_path_to_every_year_folder_and_the_all_channels_category(string externalId)
        => Assert.True(Channels.CatalogIndexWalk.ShouldOpen(externalId));

    [Theory]
    [InlineData("flt:0:all")] // newest-200 subsets of titles the year folders already reach
    [InlineData("flt:0:g4")]
    [InlineData("shw:S1")] // the show itself is what search finds; its seasons/episodes aren't walked
    [InlineData("ssn:S1")]
    [InlineData("mov:M1")]
    [InlineData("col:1")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void Leaves_everything_else_alone(string? externalId)
        => Assert.False(Channels.CatalogIndexWalk.ShouldOpen(externalId));
}

public class ImageRepairTests
{
    [Theory]
    [InlineData("fea:root", true)]
    [InlineData("row:0:top", true)]
    [InlineData("dis:root", true)]
    [InlineData("cat:0", true)]
    [InlineData("flt:1:g3", true)]
    [InlineData("flt:1:y2020", true)]
    [InlineData("flt:1:years", true)]
    [InlineData("liv:root", true)]
    [InlineData("lcat:76301", true)]
    [InlineData("mov:ABC", false)]
    [InlineData("shw:ABC", false)]
    [InlineData("ssn:ABC", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void Collage_folders_are_the_ones_the_plugin_draws(string? externalId, bool expected)
        => Assert.Equal(expected, Channels.ImageRepair.IsCollageFolder(externalId));

    [Theory]
    [InlineData("mov:ABC", "https://cdn.portal.test/poster.jpg", true)]
    [InlineData("epi:ABC:SER", "http://cdn.portal.test/poster.jpg", true)]
    [InlineData("mov:ABC", "http://127.0.0.1:8096/Portalito/img?u=x", false)]
    [InlineData("mov:ABC", "/config/data/metadata/library/ab/abc/poster.jpg", false)]
    [InlineData("cat:0", "https://cdn.portal.test/poster.jpg", false)]
    [InlineData("garbage", "https://cdn.portal.test/poster.jpg", false)]
    [InlineData("mov:ABC", null, false)]
    public void Only_titles_pointing_straight_at_a_portal_cdn_are_broken(string externalId, string? path, bool expected)
        => Assert.Equal(expected, Channels.ImageRepair.IsBrokenTitlePoster(externalId, path));

    [Theory]
    [InlineData("fea:root", true)]  // Destacado holds the row folders
    [InlineData("dis:root", true)]
    [InlineData("cat:2", true)]
    [InlineData("flt:2:years", true)]
    [InlineData("liv:root", true)]
    [InlineData("row:0:top", false)] // a row holds titles, not collage folders
    [InlineData("flt:2:y2020", false)]
    [InlineData("flt:2:all", false)]
    [InlineData("lcat:76301", false)]
    [InlineData("shw:ABC", false)]
    public void Walks_only_the_folders_that_hold_collage_folders(string externalId, bool open)
        => Assert.Equal(open, Channels.ImageRepair.ShouldOpen(externalId));
}
