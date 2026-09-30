using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Jellyfin.Plugin.Portalito.Catalog;

namespace Jellyfin.Plugin.Portalito.Metadata;

/// <summary>GETs one TMDB v3 endpoint; the response text, or null on a non-success status. Testable without a network.</summary>
public interface ITmdbTransport
{
    Task<string?> GetAsync(string path, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken);
}

/// <summary>HttpClient-backed <see cref="ITmdbTransport"/> against the real TMDB v3 API (auth: the v3 key as <c>api_key</c>).</summary>
public sealed class HttpTmdbTransport : ITmdbTransport
{
    private const string BaseUrl = "https://api.themoviedb.org/3/";

    private readonly HttpClient _http;

    public HttpTmdbTransport(HttpClient http) => _http = http;

    public async Task<string?> GetAsync(string path, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        var pairs = query.Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}");
        using var response = await _http.GetAsync(new Uri($"{BaseUrl}{path}?{string.Join('&', pairs)}"), cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : null;
    }
}

/// <summary>
/// What to look a Portalito title up by: its display name, its <c>alias</c> (original/English title), year, and — when the
/// portal's <c>keyWords</c> gave one — its IMDb id, which yields an exact TMDB match with no title guessing.
/// </summary>
public sealed record TitleQuery(string Title, string? OriginalTitle, int? Year, bool IsSeries, string? ImdbId = null);

/// <summary>What TMDB can fill in. Any field may be null.</summary>
public sealed record TmdbInfo(string? Overview, string? PosterUrl, int? Year = null, double? Rating = null);

/// <summary>Picks the TMDB search result that is really the same title, or none.</summary>
public static class TmdbMatching
{
    /// <summary>
    /// A result matches when its title or original title, normalized (lowercase, no accents, letters and digits
    /// only), equals the query's title or original title. Movies must also be within a year of the query's year:
    /// Portalito's year is reliable for movies (e.g. The Dark Knight, 2008). Series only use the year to prefer the
    /// closest of several exact-title matches: a season's <c>releaseTime</c> is often its upload date, not the show's
    /// first air date (measured: "Avatar: The last airbender" T2 reports 2026). A miss is fine -- TMDB only ever
    /// fills fields Portalito left blank, so the cost of refusing a match is nothing, and of a wrong one is a wrong synopsis.
    /// </summary>
    public static JsonObject? Pick(JsonArray? results, TitleQuery query)
    {
        var wanted = new[] { query.Title, query.OriginalTitle }.Select(Normalize).Where(t => t.Length > 0).ToHashSet();
        if (wanted.Count == 0)
        {
            return null;
        }

        var candidates = (results ?? new JsonArray()).OfType<JsonObject>()
            .Where(r => new[] { Str(r, "title"), Str(r, "name"), Str(r, "original_title"), Str(r, "original_name") }
                .Any(t => wanted.Contains(Normalize(t))))
            .Select(r => (Result: r, Year: PortalJson.Year(r[query.IsSeries ? "first_air_date" : "release_date"])))
            .ToList();

        if (!query.IsSeries)
        {
            return query.Year is { } year
                ? candidates.FirstOrDefault(c => c.Year is { } y && Math.Abs(y - year) <= 1).Result
                : null;
        }

        return query.Year is { } showYear
            ? candidates.OrderBy(c => c.Year is { } y ? Math.Abs(y - showYear) : int.MaxValue).FirstOrDefault().Result
            : candidates.FirstOrDefault().Result;
    }

    public static string Normalize(string? text)
    {
        var builder = new StringBuilder();
        foreach (var ch in (text ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static string? Str(JsonObject o, string key) => PortalJson.Str(o[key]);
}

/// <summary>
/// TMDB as a fallback for what Portalito leaves blank: mostly the synopsis (the portal's <c>description</c> is usually
/// empty), sometimes the poster (owner decision 2026-09-23). The one exception is "Destacado": its configured rows
/// are TMDB lists (trending, popular, discover...) via <see cref="ListAsync"/>, but even there only titles the portal
/// actually has are listed, and what plays is always the portal's (owner decision 2026-09-30).
/// </summary>
public sealed class TmdbClient
{
    private const string Language = "es-MX";
    private const string FallbackLanguage = "en-US";
    internal const string PosterBase = "https://image.tmdb.org/t/p/w342";

    private static readonly TmdbInfo NoMatch = new(null, null);

    private readonly ITmdbTransport _transport;
    private readonly string _apiKey;

    // Misses are cached too (as NoMatch), so a title TMDB doesn't know isn't looked up again on every listing.
    private readonly TtlCache<TmdbInfo> _cache;

    // Trending/popular move during the day; six hours keeps a row fresh without refetching on every listing.
    private readonly TtlCache<IReadOnlyList<TmdbEntry>> _lists;

    public TmdbClient(ITmdbTransport transport, string apiKey, TimeProvider clock)
    {
        _transport = transport;
        _apiKey = apiKey;
        _cache = new TtlCache<TmdbInfo>(clock, TimeSpan.FromDays(7), capacity: 20_000);
        _lists = new TtlCache<IReadOnlyList<TmdbEntry>>(clock, TimeSpan.FromHours(6), capacity: 64);
    }

    /// <summary>
    /// One configured row's TMDB list, newest-first as TMDB ranks it: each page fetched in es-MX (the titles the portal's
    /// Spanish names match best) and en-US (for the portal's English <c>alias</c>). Empty for an unknown source or when
    /// TMDB answers nothing; an empty first page isn't cached, so a transient TMDB failure isn't remembered.
    /// </summary>
    public async Task<IReadOnlyList<TmdbEntry>> ListAsync(FeaturedRow row, CancellationToken cancellationToken)
    {
        if (TmdbLists.Endpoint(row) is not { } endpoint)
        {
            return Array.Empty<TmdbEntry>();
        }

        var key = endpoint.Path + "?" + string.Join('&', endpoint.Query.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)) + "#" + endpoint.Pages;
        if (_lists.TryGet(key, out var cached))
        {
            return cached;
        }

        var spanish = new List<JsonObject>();
        var english = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (var page = 1; page <= endpoint.Pages; page++)
        {
            var es = await ListPageAsync(endpoint.Path, endpoint.Query, Language, page, cancellationToken).ConfigureAwait(false);
            if (es.Count == 0)
            {
                break;
            }

            spanish.AddRange(es);
            foreach (var r in await ListPageAsync(endpoint.Path, endpoint.Query, FallbackLanguage, page, cancellationToken).ConfigureAwait(false))
            {
                if (TmdbLists.Key(r, endpoint.IsSeries) is { } k)
                {
                    english.TryAdd(k, r);
                }
            }
        }

        var entries = TmdbLists.ToEntries(spanish, english, endpoint.IsSeries, PosterBase);
        if (entries.Count > 0)
        {
            _lists.Set(key, entries);
        }

        return entries;
    }

    private async Task<IReadOnlyList<JsonObject>> ListPageAsync(string path, IReadOnlyDictionary<string, string> query, string language, int page, CancellationToken cancellationToken)
    {
        var full = new Dictionary<string, string>(query, StringComparer.Ordinal)
        {
            ["api_key"] = _apiKey,
            ["language"] = language,
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
        };
        var body = await _transport.GetAsync(path, full, cancellationToken).ConfigureAwait(false);
        return body is null ? Array.Empty<JsonObject>() : PortalJson.Objects(JsonNode.Parse(body)?["results"]).ToArray();
    }

    /// <summary>The match's synopsis, poster, year and rating, or null when TMDB has no confident match.</summary>
    public async Task<TmdbInfo?> LookupAsync(TitleQuery query, CancellationToken cancellationToken)
    {
        var key = $"{(query.IsSeries ? "tv" : "movie")}|{query.ImdbId}|{TmdbMatching.Normalize(query.Title)}|{TmdbMatching.Normalize(query.OriginalTitle)}|{query.Year}";
        if (_cache.TryGet(key, out var cached))
        {
            return ReferenceEquals(cached, NoMatch) ? null : cached;
        }

        // An IMDb id (from the portal's keyWords) is an exact match with no title guessing. Otherwise search by the
        // original title first -- the portal often files international titles under their original name, which TMDB
        // indexes best -- then the display title. Whichever text matched is remembered for the en-US retry below.
        JsonObject? match = null;
        string? matchedText = null;
        if (!string.IsNullOrWhiteSpace(query.ImdbId))
        {
            match = await FindAsync(query.ImdbId!, query, Language, cancellationToken).ConfigureAwait(false);
        }

        if (match is null)
        {
            foreach (var text in new[] { query.OriginalTitle, query.Title }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
            {
                match = await SearchAsync(text!, query, Language, cancellationToken).ConfigureAwait(false);
                if (match is not null)
                {
                    matchedText = text;
                    break;
                }
            }
        }

        if (match is null)
        {
            _cache.Set(key, NoMatch);
            return null;
        }

        var overview = PortalJson.NonBlank(match["overview"]);
        var poster = PortalJson.NonBlank(match["poster_path"]) is { } path ? PosterBase + path : null;

        // Many titles have no es-MX synopsis but do have an English one; fetch the same match again in en-US just for
        // it (the poster and the rest stay from the es-MX response).
        if (overview is null)
        {
            var english = query.ImdbId is { } imdb && !string.IsNullOrWhiteSpace(imdb)
                ? await FindAsync(imdb, query, FallbackLanguage, cancellationToken).ConfigureAwait(false)
                : matchedText is not null
                    ? await SearchAsync(matchedText, query, FallbackLanguage, cancellationToken).ConfigureAwait(false)
                    : null;
            overview ??= PortalJson.NonBlank(english?["overview"]);
        }

        var info = new TmdbInfo(
            overview,
            poster,
            PortalJson.Year(match[query.IsSeries ? "first_air_date" : "release_date"]),
            PortalJson.Str(match["vote_average"]) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating) && rating > 0 ? rating : null);
        _cache.Set(key, info);
        return info;
    }

    /// <summary>An exact TMDB lookup by IMDb id; the single tv/movie result, or null if TMDB doesn't know the id.</summary>
    private async Task<JsonObject?> FindAsync(string imdbId, TitleQuery query, string language, CancellationToken cancellationToken)
    {
        var body = await _transport.GetAsync(
            $"find/{imdbId}",
            new Dictionary<string, string> { ["api_key"] = _apiKey, ["language"] = language, ["external_source"] = "imdb_id" },
            cancellationToken).ConfigureAwait(false);
        var results = body is null ? null : JsonNode.Parse(body)?[query.IsSeries ? "tv_results" : "movie_results"] as JsonArray;
        return results?.OfType<JsonObject>().FirstOrDefault();
    }

    private async Task<JsonObject?> SearchAsync(string text, TitleQuery query, string language, CancellationToken cancellationToken)
    {
        var body = await _transport.GetAsync(
            query.IsSeries ? "search/tv" : "search/movie",
            new Dictionary<string, string> { ["api_key"] = _apiKey, ["language"] = language, ["query"] = text },
            cancellationToken).ConfigureAwait(false);
        return body is null ? null : TmdbMatching.Pick(JsonNode.Parse(body)?["results"] as JsonArray, query);
    }
}
