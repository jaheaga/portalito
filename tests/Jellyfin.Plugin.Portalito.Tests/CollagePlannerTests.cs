using Jellyfin.Plugin.Portalito.Collage;
using Jellyfin.Plugin.Portalito.Proxy;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class CollagePlannerTests
{
    private static HashSet<string> Used(params string[] keys) => new(keys, StringComparer.Ordinal);

    // Candidates whose key is their name and whose URL is "u/<name>".
    private static IReadOnlyList<CollageImage> C(params string[] keys) => keys.Select(k => new CollageImage(k, "u/" + k)).ToList();

    private static string[] U(params string[] keys) => keys.Select(k => "u/" + k).ToArray();

    [Fact]
    public void A_folder_takes_its_best_candidates_nobody_has_taken()
    {
        var used = Used("a", "c");

        var picked = CollagePlanner.Pick(C("a", "b", "c", "d", "e", "f"), used);

        Assert.Equal(U("b", "d", "e", "f"), picked);
        Assert.Superset(Used("a", "b", "c", "d", "e", "f"), used);
    }

    [Fact]
    public void Short_of_fresh_candidates_it_tops_up_with_its_own_best_so_no_collage_is_short()
        => Assert.Equal(U("e", "a", "b", "c"), CollagePlanner.Pick(C("a", "b", "c", "d", "e"), Used("a", "b", "c", "d")));

    [Fact]
    public void A_folder_with_fewer_than_four_shows_keeps_what_it_has()
        => Assert.Equal(U("a"), CollagePlanner.Pick(C("a", "a"), Used()));

    [Fact]
    public void Two_seasons_of_one_show_count_once_in_a_collage_and_across_siblings()
    {
        var simpsons3 = new CollageImage("thesimpsons", "u/simpsons-s3");
        var simpsons5 = new CollageImage("thesimpsons", "u/simpsons-s5");
        var used = Used();

        var y1991 = CollagePlanner.Pick(new[] { simpsons3, simpsons5, new CollageImage("xmen", "u/xmen") }, used);
        var y1993 = CollagePlanner.Pick(new[] { simpsons5, new CollageImage("friends", "u/friends") }, used);

        Assert.Equal(new[] { "u/simpsons-s3", "u/xmen" }, y1991);
        Assert.Equal(new[] { "u/friends", "u/simpsons-s5" }, y1993);
    }

    [Fact]
    public void The_specific_sibling_picks_before_the_catch_all_and_keeps_its_own_titles()
    {
        // "Animación" holds everything; "Western" only its own two plus two shared ones.
        IReadOnlyList<CollageImage>[] candidates =
        {
            C("s1", "s2", "s3", "s4", "w1", "w2", "m1"),   // Animación (catch-all)
            C("s1", "w1", "w2", "s2"),                     // Western
            C("m1", "m2", "s3", "s4"),                     // Misterio
        };

        // Animación overlaps both siblings, each of them only Animación: it goes last.
        Assert.Equal(0, CollagePlanner.MostSpecificFirst(candidates)[^1]);

        var picks = CollagePlanner.PickAll(candidates, Used());

        Assert.Equal(U("s1", "w1", "w2", "s2"), picks[1]);
        Assert.Equal(U("m1", "m2", "s3", "s4"), picks[2]);
        // The catch-all is left with nothing fresh, so it repeats its own best -- still a full collage.
        Assert.Equal(4, picks[0].Count);
    }

    [Fact]
    public void Siblings_and_parents_in_one_used_set_never_repeat_while_there_is_enough()
    {
        var used = Used();
        IReadOnlyList<CollageImage>[] genres = { C("n1", "g1", "g2", "g3", "g4"), C("n1", "n2", "h1", "h2", "h3") };
        var newest = C("n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9", "n10");

        var picks = CollagePlanner.PickAll(genres, used);
        var todo = CollagePlanner.Pick(newest, used);
        var catalog = CollagePlanner.Pick(newest, used);

        var all = picks.SelectMany(p => p).Concat(todo).Concat(catalog).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Folders_without_candidates_go_last()
        => Assert.Equal(new[] { 1, 0 }, CollagePlanner.MostSpecificFirst(new[] { C(), C("a") }));

    [Fact]
    public void Interleave_takes_one_from_each_list_in_turn()
        => Assert.Equal(
            new[] { "a1", "b1", "c1", "a2", "c2", "c3" },
            CollagePlanner.Interleave(new IReadOnlyList<string>[] { new[] { "a1", "a2" }, new[] { "b1" }, new[] { "c1", "c2", "c3" } }));

    [Theory]
    [InlineData("La Casa de los Famosos 4 HD", "La Casa de los Famosos 1")]
    [InlineData("24/7 El Chapulin Colorado T1-3", "24/7 El Chapulin Colorado T4-7")]
    [InlineData("RCN HD", "RCN")]
    [InlineData("the simpsons", "The Simpsons")]
    public void Numbered_channels_and_spellings_of_one_show_share_a_key(string a, string b)
        => Assert.Equal(CollageImage.KeyFor(a), CollageImage.KeyFor(b));

    [Fact]
    public void Different_shows_keep_different_keys()
        => Assert.NotEqual(CollageImage.KeyFor("Caracol HD"), CollageImage.KeyFor("RCN HD"));

    [Fact]
    public void A_title_with_no_letters_is_its_own_key()
        => Assert.Equal("1917", CollageImage.KeyFor("1917"));
}

public class ImageTypesTests
{
    [Theory]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("image/jpg", "image/jpeg")]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData("image/webp", "image/webp")]
    [InlineData("image/*", null)]
    [InlineData("application/octet-stream", null)]
    [InlineData(null, null)]
    public void Only_types_jellyfin_can_save_pass_through(string? given, string? expected)
        => Assert.Equal(expected, ImageTypes.Normalize(given));

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39 }, "image/gif")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { }, "image/jpeg")]
    public void Sniffs_the_format_from_the_first_bytes(byte[] head, string expected)
        => Assert.Equal(expected, ImageTypes.Sniff(head));
}
