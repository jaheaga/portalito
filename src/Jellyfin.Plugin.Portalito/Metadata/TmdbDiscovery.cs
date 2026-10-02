using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;

namespace Jellyfin.Plugin.Portalito.Metadata;

/// <summary>
/// One configured "Destacado" row: a label, a TMDB list <see cref="Source"/> (see <see cref="TmdbLists.Sources"/>) and
/// its optional parameters (<c>window</c>, <c>type</c>, <c>country</c>, <c>lang</c>, <c>genre</c>, <c>nogenre</c>,
/// <c>network</c>, <c>provider</c>, <c>region</c>, <c>sort</c>, <c>year</c>, <c>minvotes</c>, <c>pages</c>).
/// </summary>
public sealed record FeaturedRow(string Label, string Source, IReadOnlyDictionary<string, string> Params);

/// <summary>
/// One title from a TMDB list: its localized (es-MX) and English titles plus the original one, so it can be matched
/// against the portal, whose <c>name</c> is usually Spanish and <c>alias</c> usually English. <see cref="Rating"/> is
/// TMDB's vote average (0-10), null when too few people voted for it to mean anything (<see cref="TmdbLists.MinRatingVotes"/>).
/// </summary>
public sealed record TmdbEntry(string Id, bool IsSeries, string? Title, string? EnglishTitle, string? OriginalTitle, int? Year, string? PosterUrl, double? Rating = null);

/// <summary>Maps a <see cref="FeaturedRow"/> to the TMDB v3 endpoint that lists it, and parses that listing.</summary>
public static class TmdbLists
{
    /// <summary>
    /// A vote average from fewer votes isn't used: Destacado rows are ordered by rating, and an upcoming film's 10.0 from
    /// two votes would otherwise lead its row.
    /// </summary>
    public const int MinRatingVotes = 10;

    /// <summary>Every supported row source, with its TMDB path and whether it lists series (null: both, per result).</summary>
    public static readonly IReadOnlyDictionary<string, (string Path, bool? IsSeries)> Sources = new Dictionary<string, (string, bool?)>(StringComparer.OrdinalIgnoreCase)
    {
        ["trending"] = ("trending", null), // path completed with {type}/{window}
        ["popular-movies"] = ("movie/popular", false),
        ["popular-tv"] = ("tv/popular", true),
        ["top-movies"] = ("movie/top_rated", false),
        ["top-tv"] = ("tv/top_rated", true),
        ["upcoming"] = ("movie/upcoming", false),
        ["now-playing"] = ("movie/now_playing", false),
        ["airing-today"] = ("tv/airing_today", true),
        ["on-air"] = ("tv/on_the_air", true),
        ["discover-movies"] = ("discover/movie", false),
        ["discover-tv"] = ("discover/tv", true),
    };

    /// <summary>
    /// Streaming platforms by name, as TMDB <b>networks</b> (who made a series: <c>network=netflix</c> lists Netflix
    /// originals; series only). Ids checked against TMDB's <c>/network/{id}</c> on 2026-10-01; "hbo" covers HBO and HBO Max.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Networks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["netflix"] = "213",
        ["apple"] = "2552",
        ["disney"] = "2739",
        ["hbo"] = "49|3186",
        ["max"] = "3186",
        ["prime"] = "1024",
        ["paramount"] = "4330",
        ["hulu"] = "453",
        ["peacock"] = "3353",
        ["crunchyroll"] = "1112",
    };

    /// <summary>
    /// Streaming platforms by name, as TMDB <b>watch providers</b> (where a title can be streamed in <c>region</c>, licensed
    /// titles included; movies and series). Ids from TMDB's <c>/watch/providers/tv?watch_region=CO</c> on 2026-10-01;
    /// Prime Video is 9 in the US and 119 elsewhere, so both.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["netflix"] = "8",
        ["prime"] = "9|119",
        ["apple"] = "350",
        ["disney"] = "337",
        ["hbo"] = "1899",
        ["max"] = "1899",
        ["paramount"] = "531",
        ["vix"] = "457",
        ["crunchyroll"] = "283",
        ["mubi"] = "11",
    };

    /// <summary>The country a <c>provider=</c> row looks in when it doesn't say (<c>region=</c>, an ISO 3166-1 code).</summary>
    public const string DefaultRegion = "US";

    /// <summary>Pages of 20 fetched per row unless the row says otherwise (bounded: each title costs portal searches).</summary>
    public const int DefaultPages = 2;

    public const int MaxPages = 3;

    public static bool IsKnownSource(string source) => Sources.ContainsKey(source);

    /// <summary>The TMDB path, the query parameters (without api_key/language/page) and the page count for a row.</summary>
    public static (string Path, bool? IsSeries, Dictionary<string, string> Query, int Pages)? Endpoint(FeaturedRow row)
    {
        if (!Sources.TryGetValue(row.Source, out var source))
        {
            return null;
        }

        var p = row.Params;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = source.Path;
        var isSeries = source.IsSeries;
        if (row.Source.Equals("trending", StringComparison.OrdinalIgnoreCase))
        {
            var type = Get(p, "type") is "movie" or "tv" ? Get(p, "type")! : "all";
            var window = Get(p, "window") is "day" ? "day" : "week";
            path = $"trending/{type}/{window}";
            isSeries = type switch { "movie" => false, "tv" => true, _ => null };
        }
        else if (path.StartsWith("discover/", StringComparison.Ordinal))
        {
            query["sort_by"] = Get(p, "sort") ?? "popularity.desc";
            Add(query, "with_origin_country", Or(Get(p, "country")));
            Add(query, "with_original_language", Or(Get(p, "lang")));
            Add(query, "with_genres", Or(Get(p, "genre")));
            Add(query, "without_genres", Get(p, "nogenre"));
            Add(query, "with_networks", Ids(Get(p, "network"), Networks));
            if (Ids(Get(p, "provider"), Providers) is { } providers)
            {
                // Only titles included in the subscription, not ones rented or bought there.
                query["with_watch_providers"] = providers;
                query["watch_region"] = Get(p, "region")?.ToUpperInvariant() ?? DefaultRegion;
                query["with_watch_monetization_types"] = "flatrate";
            }

            Add(query, "vote_count.gte", Get(p, "minvotes"));
            Add(query, isSeries == true ? "first_air_date_year" : "primary_release_year", Get(p, "year"));
        }

        var pages = int.TryParse(Get(p, "pages"), out var n) ? Math.Clamp(n, 1, MaxPages) : DefaultPages;
        return (path, isSeries, query, pages);
    }

    /// <summary>
    /// Turns one list's result objects (es-MX) into entries, taking each title's English name from the en-US results of
    /// the same list (keyed by media type + id). People (in "trending/all") are skipped; duplicates keep the first.
    /// </summary>
    public static IReadOnlyList<TmdbEntry> ToEntries(IEnumerable<JsonObject> spanish, IReadOnlyDictionary<string, JsonObject> english, bool? isSeries, string posterBase)
    {
        var entries = new List<TmdbEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in spanish)
        {
            var mediaType = PortalJson.Str(r["media_type"]) ?? (isSeries == true ? "tv" : isSeries == false ? "movie" : null);
            var id = PortalJson.Str(r["id"]);
            if (mediaType is not ("movie" or "tv") || id is null || !seen.Add(mediaType + ":" + id))
            {
                continue;
            }

            var series = mediaType == "tv";
            english.TryGetValue(mediaType + ":" + id, out var en);
            entries.Add(new TmdbEntry(
                id,
                series,
                PortalJson.NonBlank(r[series ? "name" : "title"]),
                en is null ? null : PortalJson.NonBlank(en[series ? "name" : "title"]),
                PortalJson.NonBlank(r[series ? "original_name" : "original_title"]),
                PortalJson.Year(r[series ? "first_air_date" : "release_date"]),
                PortalJson.NonBlank(r["poster_path"]) is { } poster ? posterBase + poster : null,
                Rating(r)));
        }

        return entries;
    }

    /// <summary>A result's vote average, or null when it has none or fewer than <see cref="MinRatingVotes"/> votes.</summary>
    public static double? Rating(JsonObject result)
        => PortalJson.Int(result["vote_count"]) is >= MinRatingVotes
           && PortalJson.Str(result["vote_average"]) is { } text
           && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var average)
           && average > 0
            ? Math.Round(average, 1)
            : null;

    /// <summary>The key a result is filed under when pairing the es-MX and en-US listings.</summary>
    public static string? Key(JsonObject result, bool? isSeries)
    {
        var mediaType = PortalJson.Str(result["media_type"]) ?? (isSeries == true ? "tv" : isSeries == false ? "movie" : null);
        var id = PortalJson.Str(result["id"]);
        return mediaType is null || id is null ? null : mediaType + ":" + id;
    }

    private static string? Get(IReadOnlyDictionary<string, string> p, string key)
        => p.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    /// <summary>Comma-separated platform names or raw TMDB ids, as a TMDB "or" list ("netflix,1024" → "213|1024"); unknown names dropped.</summary>
    private static string? Ids(string? value, IReadOnlyDictionary<string, string> names)
    {
        if (value is null)
        {
            return null;
        }

        var ids = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(v => names.TryGetValue(v, out var known) ? known.Split('|') : v.All(char.IsAsciiDigit) ? new[] { v } : Array.Empty<string>())
            .Distinct()
            .ToList();
        return ids.Count == 0 ? null : string.Join('|', ids);
    }

    // Config lists alternatives with commas ("JP,KR"); TMDB's discover filters take "|" for OR.
    private static string? Or(string? value) => value?.Replace(',', '|');

    private static void Add(Dictionary<string, string> query, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            query[key] = value;
        }
    }
}
