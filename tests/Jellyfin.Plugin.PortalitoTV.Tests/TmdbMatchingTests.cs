using Jellyfin.Plugin.PortalitoTV.Content;
using Jellyfin.Plugin.PortalitoTV.Metadata;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

public class TmdbMatchingTests
{
    private static Dictionary<int, int> D(params (int Season, int N)[] items) => items.ToDictionary(i => i.Season, i => i.N);

    [Fact]
    public void SameEpisodeCountIsTrusted()
    {
        var trusted = TmdbClient.TrustedSeasons(D((1, 10), (2, 22)), D((1, 10), (2, 22)), D((1, 10), (2, 22)));
        Assert.Equal(new[] { 1, 2 }, trusted.OrderBy(x => x));
    }

    [Fact]
    public void MissingEpisodesWithPreservedNumbersAreTrusted()
    {
        // Season 28: 21 episodes but the highest number is 22 (number 13 is missing) and TMDB has 22.
        // Season 3: 16 episodes, highest number 22 (15..20 are missing) and TMDB has 22.
        var trusted = TmdbClient.TrustedSeasons(D((28, 21), (3, 16)), D((28, 22), (3, 22)), D((28, 22), (3, 22)));
        Assert.Contains(28, trusted);
        Assert.Contains(3, trusted);
    }

    [Fact]
    public void ExtraOrFewerEpisodesWithoutGapsAreNotTrusted()
    {
        // Portalito has more than TMDB (21 vs 18) or a different count (14 vs 13) and the numbering is continuous: there is no way to tell which one is extra.
        var trusted = TmdbClient.TrustedSeasons(D((36, 21), (6, 14)), D((36, 21), (6, 14)), D((36, 18), (6, 13)));
        Assert.Empty(trusted);
    }

    [Fact]
    public void GapRuleIsOffWithoutSpansAndIgnoresMissingTmdbSeasons()
    {
        Assert.Empty(TmdbClient.TrustedSeasons(D((3, 16)), null, D((3, 22))));
        Assert.Empty(TmdbClient.TrustedSeasons(D((9, 10)), D((9, 10)), D((1, 10))));
    }

    [Fact]
    public void SeasonOptionsListSameSeasonThenEqualCountsByDistance()
    {
        var tmdb = D((1, 9), (2, 20), (3, 22), (4, 16), (5, 16), (6, 26), (7, 16));
        var options = ContentCatalog.SeasonOptions(tmdb, season: 6, count: 16, span: 16, mapped: null);
        Assert.Equal(new[] { 6, 5, 7, 4 }, options.Select(o => o.Season)); // the same season, then the 16-episode ones, nearest first
        Assert.Equal(26, options[0].Episodes);
    }

    [Fact]
    public void SeasonOptionsKeepTheMappedSeasonAndAreCapped()
    {
        var tmdb = Enumerable.Range(1, 30).ToDictionary(s => s, _ => 22);
        var options = ContentCatalog.SeasonOptions(tmdb, season: 28, count: 22, span: 22, mapped: 3);
        Assert.Equal(28, options[0].Season);
        Assert.Equal(3, options[1].Season);
        Assert.Equal(10, options.Count); // the same one + the assigned one + 8 by equal episode count
        Assert.Equal(options.Count, options.Select(o => o.Season).Distinct().Count());
    }

    [Fact]
    public void SeasonOptionsWithoutSameSeasonStillSuggestEqualCounts()
    {
        var options = ContentCatalog.SeasonOptions(D((1, 13), (2, 13)), season: 40, count: 13, span: 13, mapped: null);
        Assert.Equal(new[] { 2, 1 }, options.Select(o => o.Season));
    }
}
