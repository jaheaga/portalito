using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Metadata;
using Jellyfin.Plugin.Portalito.Portal;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class DiscoveryBrowserTests
{
    private static JsonObject Tv(int id, string name, string original, string firstAir, string poster = "/p.jpg")
        => new() { ["id"] = id, ["name"] = name, ["original_name"] = original, ["first_air_date"] = firstAir, ["poster_path"] = poster };

    private static JsonObject Movie(int id, string title, string original, string released)
        => new() { ["id"] = id, ["title"] = title, ["original_title"] = original, ["release_date"] = released };

    private static string Results(params JsonObject[] results)
        => new JsonObject { ["results"] = new JsonArray(results.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString();

    private static JsonObject Asset(string id, string name, string alias, string programType, string releaseTime)
        => new() { ["contentId"] = id, ["name"] = name, ["alias"] = alias, ["programType"] = programType, ["releaseTime"] = releaseTime };

    [Fact]
    public void Rows_parse_label_source_and_parameters_skipping_bad_ones()
    {
        var rows = DiscoveryBrowser.ParseRows(
            "Tendencias | trending | window=day type=tv; Series coreanas | Discover-TV | country=KR\nBroken | nosuchsource;  | trending ; Solo | popular-movies");

        Assert.Equal(new[] { "Tendencias", "Series coreanas", "Solo" }, rows.Select(r => r.Label));
        Assert.Equal("discover-tv", rows[1].Source); // lowercased
        Assert.Equal("day", rows[0].Params["window"]);
        Assert.Equal("KR", rows[1].Params["country"]);
        Assert.Empty(rows[2].Params);
    }

    [Fact]
    public void The_default_rows_are_the_seeded_tmdb_rows_with_the_platform_rows()
    {
        var rows = DiscoveryBrowser.ParseRows(Configuration.PluginConfiguration.DefaultFeaturedRows);

        Assert.Equal(
            new[]
            {
                "Tendencias", "Estrenos en cine", "Próximamente", "Películas populares", "Series populares",
                "Originales Netflix", "Apple TV+", "Disney+", "HBO", "Prime Video", "Paramount+", "En emisión hoy",
                "Anime del momento", "Películas de anime", "Series coreanas", "Películas mejor valoradas", "Series mejor valoradas",
            },
            rows.Select(r => r.Label));
        Assert.All(rows, r => Assert.NotNull(TmdbLists.Endpoint(r)));
        Assert.Equal("213", TmdbLists.Endpoint(rows[5])!.Value.Query["with_networks"]);
        Assert.Equal("100", TmdbLists.Endpoint(rows[5])!.Value.Query["vote_count.gte"]);
        Assert.Equal("JP|KR", TmdbLists.Endpoint(rows[12])!.Value.Query["with_origin_country"]);
        Assert.Equal("16,10764,10767", TmdbLists.Endpoint(rows[14])!.Value.Query["without_genres"]);
        Assert.Equal(Configuration.PluginConfiguration.DefaultFeaturedRows, new Configuration.PluginConfiguration().FeaturedRows);
    }

    [Fact]
    public void A_network_row_lists_a_platforms_originals_by_name_or_id()
    {
        var endpoint = TmdbLists.Endpoint(DiscoveryBrowser.ParseRows("HBO | discover-tv | network=HBO,1024,nope").Single())!.Value;

        Assert.Equal("49|3186|1024", endpoint.Query["with_networks"]);
        Assert.False(endpoint.Query.ContainsKey("with_watch_providers"));
    }

    [Fact]
    public void A_provider_row_lists_what_streams_there_in_a_region()
    {
        var inColombia = TmdbLists.Endpoint(DiscoveryBrowser.ParseRows("En Prime | discover-movies | provider=prime region=co").Single())!.Value;
        var noRegion = TmdbLists.Endpoint(DiscoveryBrowser.ParseRows("Netflix | discover-tv | provider=netflix").Single())!.Value;
        var unknown = TmdbLists.Endpoint(DiscoveryBrowser.ParseRows("X | discover-tv | provider=unknown").Single())!.Value;

        Assert.Equal("9|119", inColombia.Query["with_watch_providers"]);
        Assert.Equal("CO", inColombia.Query["watch_region"]);
        Assert.Equal("flatrate", inColombia.Query["with_watch_monetization_types"]);
        Assert.Equal(TmdbLists.DefaultRegion, noRegion.Query["watch_region"]);
        Assert.False(unknown.Query.ContainsKey("watch_region"));
    }

    [Fact]
    public void Trending_defaults_to_all_titles_this_week()
    {
        var endpoint = TmdbLists.Endpoint(new FeaturedRow("T", "trending", new Dictionary<string, string>()))!.Value;

        Assert.Equal("trending/all/week", endpoint.Path);
        Assert.Null(endpoint.IsSeries);
        Assert.Equal(TmdbLists.DefaultPages, endpoint.Pages);
    }

    [Fact]
    public void Discover_maps_comma_alternatives_to_tmdb_or_filters()
    {
        var row = DiscoveryBrowser.ParseRows("Anime | discover-tv | country=JP,KR genre=16 nogenre=10764 year=2024 pages=9").Single();
        var endpoint = TmdbLists.Endpoint(row)!.Value;

        Assert.Equal("discover/tv", endpoint.Path);
        Assert.True(endpoint.IsSeries);
        Assert.Equal("JP|KR", endpoint.Query["with_origin_country"]);
        Assert.Equal("16", endpoint.Query["with_genres"]);
        Assert.Equal("10764", endpoint.Query["without_genres"]);
        Assert.Equal("2024", endpoint.Query["first_air_date_year"]);
        Assert.Equal("popularity.desc", endpoint.Query["sort_by"]);
        Assert.Equal(TmdbLists.MaxPages, endpoint.Pages); // clamped
    }

    [Fact]
    public async Task A_list_pairs_spanish_and_english_titles_and_skips_people()
    {
        var tmdb = new FakeTmdbTransport
        {
            Body = (path, q) => q["page"] != "1" ? Results()
                : q["language"] == "es-MX"
                    ? Results(
                        new JsonObject { ["id"] = 1, ["media_type"] = "tv", ["name"] = "El juego del calamar", ["original_name"] = "오징어 게임", ["first_air_date"] = "2021-09-17", ["poster_path"] = "/sq.jpg" },
                        new JsonObject { ["id"] = 9, ["media_type"] = "person", ["name"] = "Somebody" })
                    : Results(new JsonObject { ["id"] = 1, ["media_type"] = "tv", ["name"] = "Squid Game" }),
        };
        var client = new TmdbClient(tmdb, "key", TimeProvider.System);

        var entries = await client.ListAsync(new FeaturedRow("T", "trending", new Dictionary<string, string>()), CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsSeries);
        Assert.Equal("El juego del calamar", entry.Title);
        Assert.Equal("Squid Game", entry.EnglishTitle);
        Assert.Equal(2021, entry.Year);
        Assert.Equal("https://image.tmdb.org/t/p/w342/sq.jpg", entry.PosterUrl);
        Assert.All(tmdb.Requests, r => Assert.Equal("trending/all/week", r.Path));
    }

    [Fact]
    public void A_series_matches_by_alias_without_its_season_marker_preferring_the_closest_season()
    {
        var entry = new TmdbEntry("1", true, "El juego del calamar", "Squid Game", "오징어 게임", 2021, null);
        var candidates = new[]
        {
            Asset("S3", "El juego del calamar T3", "Squid Game S3", "teleplay", "2025-06-27"),
            Asset("S1", "El juego del calamar T1", "Squid Game S1", "teleplay", "2021-09-17"),
            Asset("M", "Squid Game", "Squid Game", "movie", "2021-01-01"), // wrong kind
        };

        Assert.Equal("S1", PortalJson.Str(DiscoveryBrowser.PickMatch(candidates, entry)!["contentId"]));
    }

    [Fact]
    public void A_movie_must_be_within_a_year()
    {
        var entry = new TmdbEntry("2", false, "Duna", "Dune", "Dune", 2021, null);
        var candidates = new[]
        {
            Asset("OLD", "Dune", "Dune", "movie", "1985-03-04"),
            Asset("NEW", "Duna", "Dune", "movie", "2021-10-22"),
        };

        Assert.Equal("NEW", PortalJson.Str(DiscoveryBrowser.PickMatch(candidates, entry)!["contentId"]));
        Assert.Null(DiscoveryBrowser.PickMatch(new[] { candidates[0] }, entry));
    }

    [Fact]
    public async Task A_row_keeps_only_portal_matches_in_tmdb_order_one_per_show()
    {
        var tmdb = new FakeTmdbTransport
        {
            Body = (path, q) => q["page"] != "1" ? Results() : Results(
                Tv(1, q["language"] == "es-MX" ? "Solo Leveling" : "Solo Leveling", "俺だけレベルアップな件", "2024-01-07"),
                Tv(2, q["language"] == "es-MX" ? "Serie inexistente" : "Missing Show", "없는 쇼", "2023-01-01"),
                Tv(3, q["language"] == "es-MX" ? "El juego del calamar" : "Squid Game", "오징어 게임", "2021-09-17")),
        };
        var portalTransport = new FakePortalTransport();
        portalTransport.Handler = r =>
        {
            if (r.Path == "v8/active")
            {
                return portalTransport.Ok(new JsonObject { ["userId"] = "u1", ["userToken"] = "tok" });
            }

            var items = r.Body["value"]!.GetValue<string>() switch
            {
                "Solo Leveling" => new[] { Asset("SL1", "Solo Leveling T1", "Ore dake reberu appu na ken S1", "teleplay", "2024-01-06"), Asset("SL2", "Solo Leveling T2", "Solo Leveling S2", "teleplay", "2025-01-06") },
                "El juego del calamar" => new[] { Asset("SQ1", "El juego del calamar T1", "Squid Game S1", "teleplay", "2021-09-17") },
                _ => Array.Empty<JsonObject>(),
            };
            return portalTransport.Ok(new JsonObject
            {
                ["searchItemList"] = new JsonArray(new JsonObject { ["itemList"] = new JsonArray(items.Select(i => (JsonNode?)i).ToArray()) }),
            });
        };
        var portal = new PortalClient(
            new PortalOptions { TripleDesKeyHex = PortalCipherTests.TestKeyHex, Hosts = new[] { "host-a.test" }, AppId = "com.example.app", DeviceSn = "TESTSN0001", Portal = "portal1" },
            portalTransport);
        var browser = new DiscoveryBrowser(new TmdbClient(tmdb, "key", TimeProvider.System), TimeProvider.System, DiscoveryBrowser.ParseRows("Anime | popular-tv"));

        var row = await browser.RowAsync(portal, 0, CancellationToken.None);

        Assert.Equal(new[] { "SL1", "SQ1" }, row.Select(i => PortalJson.Str(i["contentId"]))); // unmatched show dropped, order kept
        var searches = portalTransport.Count("v3/searchByName");
        await browser.RowAsync(portal, 0, CancellationToken.None);
        Assert.Equal(searches, portalTransport.Count("v3/searchByName")); // cached
    }
}
