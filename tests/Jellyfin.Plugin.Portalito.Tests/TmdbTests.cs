using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Metadata;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>Answers TMDB searches from a script keyed by (path, query text), recording every request.</summary>
internal sealed class FakeTmdbTransport : ITmdbTransport
{
    public List<(string Path, IReadOnlyDictionary<string, string> Query)> Requests { get; } = new();

    /// <summary>Scripts <c>search/*</c> responses by (path, query text) -> results array. Language-agnostic.</summary>
    public Func<string, string, JsonArray?> Results { get; set; } = (_, _) => null;

    /// <summary>Optional full-body override, given the path and the whole query (incl. language / external_source);
    /// takes precedence when set. Used for <c>find/*</c> and language-sensitive tests.</summary>
    public Func<string, IReadOnlyDictionary<string, string>, string?>? Body { get; set; }

    public Task<string?> GetAsync(string path, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add((path, query));
        }

        if (Body is { } body)
        {
            return Task.FromResult(body(path, query));
        }

        // Default: only search paths are scripted via Results; find paths answer "no match" unless Body handles them.
        if (!path.StartsWith("search/", StringComparison.Ordinal))
        {
            return Task.FromResult<string?>(null);
        }

        var results = Results(path, query["query"]);
        return Task.FromResult(results is null ? null : new JsonObject { ["results"] = results }.ToJsonString());
    }
}

public class TmdbMatchingTests
{
    private static JsonObject Movie(string title, string? date, string? original = null) => new()
    {
        ["title"] = title,
        ["original_title"] = original ?? title,
        ["release_date"] = date,
    };

    private static JsonObject Show(string name, string? date) => new() { ["name"] = name, ["original_name"] = name, ["first_air_date"] = date };

    [Fact]
    public void A_movie_matches_on_title_and_year_within_one()
    {
        var results = new JsonArray(Movie("The Dark Knight Rises", "2012-07-20"), Movie("The Dark Knight", "2008-07-16"));

        var pick = TmdbMatching.Pick(results, new TitleQuery("Batman: El Caballero de La Noche", "The Dark Knight", 2008, IsSeries: false));

        Assert.Equal("The Dark Knight", pick!["title"]!.GetValue<string>());
        Assert.NotNull(TmdbMatching.Pick(results, new TitleQuery("x", "The Dark Knight", 2009, false)));
    }

    [Fact]
    public void A_movie_two_years_off_or_with_no_year_is_not_trusted()
    {
        var results = new JsonArray(Movie("The Dark Knight", "2008-07-16"));

        Assert.Null(TmdbMatching.Pick(results, new TitleQuery("The Dark Knight", null, 2010, false)));
        Assert.Null(TmdbMatching.Pick(results, new TitleQuery("The Dark Knight", null, null, false)));
    }

    [Fact]
    public void Titles_compare_without_case_accents_or_punctuation()
    {
        var results = new JsonArray(Movie("Amélie", "2001-04-25"));

        Assert.NotNull(TmdbMatching.Pick(results, new TitleQuery("AMELIE!", null, 2001, false)));
        Assert.Null(TmdbMatching.Pick(results, new TitleQuery("Amelie 2", null, 2001, false)));
    }

    [Fact]
    public void A_series_matches_on_title_alone_and_prefers_the_closest_year()
    {
        // A season's releaseTime is often its upload date (Avatar T2 reports 2026), so the year can't gate the match.
        var results = new JsonArray(Show("Avatar: The Last Airbender", "2005-02-21"), Show("Avatar: The Last Airbender", "2024-02-22"));

        var pick = TmdbMatching.Pick(results, new TitleQuery("Avatar: The last airbender", null, 2026, IsSeries: true));

        Assert.Equal("2024-02-22", pick!["first_air_date"]!.GetValue<string>());
    }

    [Fact]
    public void No_exact_title_means_no_match()
    {
        var results = new JsonArray(Show("Avatar: The Legend of Korra", "2012-04-14"));

        Assert.Null(TmdbMatching.Pick(results, new TitleQuery("Avatar: The last airbender", null, null, true)));
    }
}

public class TmdbClientTests
{
    private readonly FakeTmdbTransport _transport = new();
    private readonly TmdbClient _client;

    public TmdbClientTests() => _client = new TmdbClient(_transport, "KEY123", new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000)));

    private static JsonArray Movies(params (string Title, string Date, string Overview, string? Poster)[] movies)
        => new(movies.Select(m => (JsonNode?)new JsonObject
        {
            ["title"] = m.Title,
            ["original_title"] = m.Title,
            ["release_date"] = m.Date,
            ["overview"] = m.Overview,
            ["poster_path"] = m.Poster,
        }).ToArray());

    [Fact]
    public async Task Searches_the_original_title_first_in_mexican_spanish_with_the_key()
    {
        _transport.Results = (_, q) => q == "The Dark Knight" ? Movies(("The Dark Knight", "2008-07-16", "Batman se enfrenta al Joker.", "/abc.jpg")) : null;

        var info = await _client.LookupAsync(new TitleQuery("Batman: El Caballero de La Noche", "The Dark Knight", 2008, false), default);

        Assert.Equal("Batman se enfrenta al Joker.", info!.Overview);
        Assert.Equal("https://image.tmdb.org/t/p/w342/abc.jpg", info.PosterUrl);
        var (path, query) = Assert.Single(_transport.Requests);
        Assert.Equal("search/movie", path);
        Assert.Equal("KEY123", query["api_key"]);
        Assert.Equal("es-MX", query["language"]);
        Assert.Equal("The Dark Knight", query["query"]);
    }

    [Fact]
    public async Task Falls_back_to_the_display_title_and_searches_tv_for_series()
    {
        _transport.Results = (_, q) => q == "Apolo bajo fuego"
            ? new JsonArray(new JsonObject { ["name"] = "Apolo bajo fuego", ["first_air_date"] = "2023-01-01", ["overview"] = "Serie." })
            : new JsonArray();

        var info = await _client.LookupAsync(new TitleQuery("Apolo bajo fuego", "Apollo Under Fire", 2025, IsSeries: true), default);

        Assert.Equal("Serie.", info!.Overview);
        Assert.Equal(new[] { "Apollo Under Fire", "Apolo bajo fuego" }, _transport.Requests.Select(r => r.Query["query"]));
        Assert.All(_transport.Requests, r => Assert.Equal("search/tv", r.Path));
    }

    [Fact]
    public async Task Hits_and_misses_are_both_cached()
    {
        _transport.Results = (_, q) => q == "Known" ? Movies(("Known", "2020-01-01", "Sinopsis.", null)) : new JsonArray();
        var known = new TitleQuery("Known", null, 2020, false);
        var unknown = new TitleQuery("Unknown", null, 2020, false);

        Assert.NotNull(await _client.LookupAsync(known, default));
        Assert.Null(await _client.LookupAsync(unknown, default));
        Assert.NotNull(await _client.LookupAsync(known, default));
        Assert.Null(await _client.LookupAsync(unknown, default));

        Assert.Equal(2, _transport.Requests.Count);
    }

    [Fact]
    public async Task A_failed_request_is_a_miss()
    {
        _transport.Results = (_, _) => null;

        Assert.Null(await _client.LookupAsync(new TitleQuery("Anything", null, 2020, false), default));
    }

    [Fact]
    public async Task An_imdb_id_takes_the_exact_find_result_without_searching_by_title()
    {
        _transport.Body = (path, q) => path == "find/tt0468569" && q["external_source"] == "imdb_id"
            ? new JsonObject { ["movie_results"] = new JsonArray(new JsonObject { ["title"] = "The Dark Knight", ["release_date"] = "2008-07-16", ["overview"] = "Batman.", ["poster_path"] = "/dk.jpg", ["vote_average"] = 8.5 }) }.ToJsonString()
            : null;

        var info = await _client.LookupAsync(new TitleQuery("cualquier título", "whatever", 2008, IsSeries: false, ImdbId: "tt0468569"), default);

        Assert.Equal("Batman.", info!.Overview);
        Assert.Equal("https://image.tmdb.org/t/p/w342/dk.jpg", info.PosterUrl);
        Assert.Equal(2008, info.Year);
        Assert.Equal(8.5, info.Rating);
        Assert.Equal("find/tt0468569", Assert.Single(_transport.Requests).Path); // no search/* call
    }

    [Fact]
    public async Task Find_reads_tv_results_for_a_series()
    {
        _transport.Body = (path, _) => path == "find/tt0417299"
            ? new JsonObject { ["movie_results"] = new JsonArray(), ["tv_results"] = new JsonArray(new JsonObject { ["name"] = "Avatar", ["first_air_date"] = "2005-02-21", ["overview"] = "Serie." }) }.ToJsonString()
            : null;

        var info = await _client.LookupAsync(new TitleQuery("Avatar", null, 2005, IsSeries: true, ImdbId: "tt0417299"), default);

        Assert.Equal("Serie.", info!.Overview);
        Assert.Equal(2005, info.Year);
    }

    [Fact]
    public async Task A_blank_spanish_overview_is_filled_from_the_english_one()
    {
        // es-MX has the match but no overview; en-US carries it. Poster/year still come from the first response.
        _transport.Body = (path, q) =>
        {
            if (path != "find/tt0468569")
            {
                return null;
            }

            var overview = q["language"] == "en-US" ? "The Dark Knight faces the Joker." : string.Empty;
            return new JsonObject { ["movie_results"] = new JsonArray(new JsonObject { ["title"] = "The Dark Knight", ["release_date"] = "2008-07-16", ["overview"] = overview, ["poster_path"] = "/dk.jpg" }) }.ToJsonString();
        };

        var info = await _client.LookupAsync(new TitleQuery("x", null, 2008, IsSeries: false, ImdbId: "tt0468569"), default);

        Assert.Equal("The Dark Knight faces the Joker.", info!.Overview);
        Assert.Equal("https://image.tmdb.org/t/p/w342/dk.jpg", info.PosterUrl);
        Assert.Equal(new[] { "es-MX", "en-US" }, _transport.Requests.Select(r => r.Query["language"]));
    }

    [Fact]
    public async Task A_blank_spanish_search_overview_retries_the_matched_title_in_english()
    {
        _transport.Body = (path, q) =>
        {
            if (path != "search/movie" || q["query"] != "Amelie")
            {
                return null;
            }

            var overview = q["language"] == "en-US" ? "A shy waitress." : string.Empty;
            return new JsonObject { ["results"] = new JsonArray(new JsonObject { ["title"] = "Amelie", ["release_date"] = "2001-04-25", ["overview"] = overview }) }.ToJsonString();
        };

        var info = await _client.LookupAsync(new TitleQuery("Amelie", null, 2001, IsSeries: false), default);

        Assert.Equal("A shy waitress.", info!.Overview);
        Assert.Equal(new[] { "es-MX", "en-US" }, _transport.Requests.Select(r => r.Query["language"]));
    }

    [Fact]
    public async Task An_imdb_id_that_tmdb_does_not_know_falls_back_to_a_title_search()
    {
        _transport.Body = (path, q) => path.StartsWith("find/", StringComparison.Ordinal)
            ? new JsonObject { ["movie_results"] = new JsonArray(), ["tv_results"] = new JsonArray() }.ToJsonString()
            : path == "search/movie" && q["query"] == "Known"
                ? new JsonObject { ["results"] = new JsonArray(new JsonObject { ["title"] = "Known", ["release_date"] = "2020-01-01", ["overview"] = "Sinopsis." }) }.ToJsonString()
                : null;

        var info = await _client.LookupAsync(new TitleQuery("Known", null, 2020, IsSeries: false, ImdbId: "tt9999999"), default);

        Assert.Equal("Sinopsis.", info!.Overview);
        Assert.Contains(_transport.Requests, r => r.Path == "find/tt9999999");
        Assert.Contains(_transport.Requests, r => r.Path == "search/movie");
    }
}

public class PortalJsonImdbTests
{
    [Theory]
    [InlineData("tt0468569", "tt0468569")]
    [InlineData("IMDb: tt1234567 | rating 9", "tt1234567")]
    [InlineData("tt0468569,tt7654321", "tt0468569")]
    [InlineData("action, drama", null)]
    [InlineData("tt123", null)]
    [InlineData("", null)]
    public void Extracts_the_first_imdb_id_from_a_string(string value, string? expected)
        => Assert.Equal(expected, Jellyfin.Plugin.Portalito.Catalog.PortalJson.ImdbId(JsonValue.Create(value)));

    [Fact]
    public void Reads_an_imdb_id_from_a_json_array()
        => Assert.Equal("tt1234567", Jellyfin.Plugin.Portalito.Catalog.PortalJson.ImdbId(new JsonArray("drama", "tt1234567")));

    [Fact]
    public void A_missing_field_has_no_imdb_id()
        => Assert.Null(Jellyfin.Plugin.Portalito.Catalog.PortalJson.ImdbId(null));

    [Fact]
    public async Task A_tmdb_error_is_not_remembered_as_no_match()
    {
        var transport = new FakeTmdbTransport();
        var client = new TmdbClient(transport, "KEY123", new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000)));
        var failing = true;
        transport.Body = (path, q) => failing
            ? throw new HttpRequestException("TMDB answered 429", null, System.Net.HttpStatusCode.TooManyRequests)
            : new JsonObject { ["results"] = new JsonArray(new JsonObject { ["title"] = "Duna", ["original_title"] = "Dune", ["release_date"] = "2021-09-15", ["overview"] = "Arrakis.", ["poster_path"] = "/d.jpg" }) }.ToJsonString();
        var query = new TitleQuery("Duna", "Dune", 2021, false);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.LookupAsync(query, default));
        failing = false;

        Assert.Equal("Arrakis.", (await client.LookupAsync(query, default))!.Overview); // looked up again, not a cached miss
    }
}

public class TmdbArtworkTests
{
    private static JsonObject Image(string path, string? language, double votes)
        => new() { ["file_path"] = path, ["iso_639_1"] = language, ["vote_average"] = votes };

    [Fact]
    public void The_backdrop_is_textless_the_thumb_titled_and_the_logo_spanish_first()
    {
        var images = new JsonObject
        {
            ["backdrops"] = new JsonArray(Image("/en.jpg", "en", 9), Image("/plain-low.jpg", null, 3), Image("/plain.jpg", null, 5), Image("/es.jpg", "es", 1)),
            ["logos"] = new JsonArray(Image("/logo-en.png", "en", 9), Image("/logo-es.png", "es", 2)),
        };

        var art = TmdbClient.PickArtwork(images);

        Assert.Equal(TmdbClient.BackdropBase + "/plain.jpg", art.BackdropUrl);
        Assert.Equal(TmdbClient.BackdropBase + "/es.jpg", art.ThumbUrl);
        Assert.Equal(TmdbClient.LogoBase + "/logo-es.png", art.LogoUrl);
    }

    [Fact]
    public void Missing_kinds_fall_back_to_what_there_is()
    {
        var art = TmdbClient.PickArtwork(new JsonObject { ["backdrops"] = new JsonArray(Image("/en.jpg", "en", 1)) });

        Assert.Equal(TmdbClient.BackdropBase + "/en.jpg", art.BackdropUrl);
        Assert.Equal(TmdbClient.BackdropBase + "/en.jpg", art.ThumbUrl);
        Assert.Null(art.LogoUrl);
        Assert.Equal(TmdbArtwork.None, TmdbClient.PickArtwork(null));
    }

    [Fact]
    public async Task A_season_with_every_still_is_kept_two_weeks_and_an_airing_one_a_day()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000));
        static JsonObject Ep(int n, string? still) => new() { ["episode_number"] = n, ["still_path"] = still };
        var transport = new FakeTmdbTransport
        {
            Body = (path, _) => path switch
            {
                "tv/7/season/1" => new JsonObject { ["episodes"] = new JsonArray(Ep(1, "/a.jpg"), Ep(2, "/b.jpg")) }.ToJsonString(),
                "tv/7/season/2" => new JsonObject { ["episodes"] = new JsonArray(Ep(1, "/c.jpg"), Ep(2, null)) }.ToJsonString(),
                _ => null,
            },
        };
        var client = new TmdbClient(transport, "KEY123", clock);

        var complete = await client.SeasonAsync(7, 1, default);
        var airing = await client.SeasonAsync(7, 2, default);
        Assert.Equal(2, complete.EpisodeCount);
        Assert.Equal(TmdbClient.StillBase + "/b.jpg", complete.Stills[2]);
        Assert.Equal(2, airing.EpisodeCount);
        Assert.False(airing.Stills.ContainsKey(2));
        Assert.Equal(TmdbSeason.None, await client.SeasonAsync(7, 9, default));

        clock.Now += TimeSpan.FromDays(2);
        var before = transport.Requests.Count;
        await client.SeasonAsync(7, 1, default);
        await client.SeasonAsync(7, 2, default);
        Assert.Equal(new[] { "tv/7/season/2" }, transport.Requests.Skip(before).Select(r => r.Path)); // only the airing one again
    }
}
