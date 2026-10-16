using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.PortalitoTV.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Metadata;

/// <summary>A series on TMDB.</summary>
/// <param name="Id">TMDB id.</param>
/// <param name="Name">Name.</param>
/// <param name="Poster">Poster path.</param>
/// <param name="Backdrop">Backdrop path.</param>
public sealed record TmdbShow(int Id, string? Name, string? Poster, string? Backdrop);

/// <summary>An episode on TMDB.</summary>
/// <param name="Name">Episode name.</param>
/// <param name="Overview">Synopsis.</param>
/// <param name="Still">Still path.</param>
public sealed record TmdbEpisode(string? Name, string? Overview, string? Still);

/// <summary>A movie on TMDB.</summary>
/// <param name="Id">TMDB id.</param>
/// <param name="Overview">Synopsis.</param>
/// <param name="Image">Image URL.</param>
public sealed record TmdbMovie(int Id, string? Overview, string? Image);

/// <summary>
/// Which TMDB numbering matches Portalito's: the broadcast one (<paramref name="GroupId"/> null) or an episode group ("Digital
/// Order"...). Only seasons with the same number of episodes are trusted; in the others Portalito's SxxEyy points at another
/// episode on TMDB and only series-level data is used.
/// </summary>
/// <param name="GroupId">The episode group id (null = broadcast order).</param>
/// <param name="GroupName">The numbering's name.</param>
/// <param name="TrustedSeasons">The Portalito seasons whose TMDB data can be used.</param>
/// <param name="SeriesOverview">The series synopsis.</param>
/// <param name="Counts">Season → episode count in this numbering.</param>
public sealed record TmdbOrdering(string? GroupId, string GroupName, IReadOnlySet<int> TrustedSeasons, string? SeriesOverview,
    IReadOnlyDictionary<int, int>? Counts = null);

/// <summary>
/// Names, synopses and images for the guide. If TMDB fails, it goes on without new data until the next pass.
/// </summary>
public sealed partial class TmdbClient
{
    /// <summary>The base URL of TMDB images.</summary>
    public const string ImageBase = "https://image.tmdb.org/t/p/";

    private static readonly string[] Languages = { "es-MX", "es-ES" };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<TmdbClient> _logger;
    private readonly object _lock = new();
    private Dictionary<string, JsonNode?>? _cache;
    private bool _dirty;
    private DateTime _downUntil;

    /// <summary>Initializes a new instance of the <see cref="TmdbClient"/> class.</summary>
    /// <param name="http">The HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TmdbClient(IHttpClientFactory http, ILogger<TmdbClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.WorkDir, "tmdb.json");

    private Dictionary<string, JsonNode?> Cache
    {
        get
        {
            lock (_lock)
            {
                return _cache ??= JsonFile.Load<Dictionary<string, JsonNode?>>(FilePath) ?? new();
            }
        }
    }

    /// <summary>Whether a name says nothing about the episode ("Episodio 3", "Episode 3", "Capítulo 3" and Portalito's fillers like "&lt;Show&gt; Season 27 - 1"): it is no use as a subtitle or a title.</summary>
    /// <param name="name">The episode name.</param>
    /// <returns>True if it is empty or a placeholder.</returns>
    public static bool IsGenericTitle(string? name) => string.IsNullOrWhiteSpace(name) || GenericTitle().IsMatch(name.Trim());

    /// <summary>The full URL of a TMDB image.</summary>
    /// <param name="path">The image path.</param>
    /// <param name="size">The size, e.g. "w780".</param>
    /// <returns>The URL, or null if there is no path.</returns>
    public static string? Image(string? path, string size) => string.IsNullOrEmpty(path) ? null : ImageBase + size + path;

    /// <summary>
    /// The Portalito seasons whose TMDB data is usable: the same number of episodes, or Portalito misses episodes but the ones it
    /// has keep their number (its highest number equals TMDB's total, e.g. a season that skips episode 13), so each episode
    /// still lands on its own number.
    /// </summary>
    /// <param name="portalSeasons">Season → episode count in Portalito.</param>
    /// <param name="portalSpans">Season → highest episode number in Portalito (null = the gaps rule isn't used).</param>
    /// <param name="tmdbCounts">Season → episodes in the TMDB numbering being evaluated.</param>
    /// <returns>The trusted seasons.</returns>
    public static HashSet<int> TrustedSeasons(IReadOnlyDictionary<int, int> portalSeasons, IReadOnlyDictionary<int, int>? portalSpans,
        IReadOnlyDictionary<int, int> tmdbCounts) =>
        portalSeasons.Where(p => tmdbCounts.TryGetValue(p.Key, out var n)
                && (n == p.Value || (portalSpans is not null && p.Value < n && portalSpans.TryGetValue(p.Key, out var span) && span == n)))
            .Select(p => p.Key).ToHashSet();

    /// <summary>Looks a series up on TMDB by its folder name.</summary>
    /// <param name="folderName">The series folder name (without the "[portalito-id]" tag).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The series, or null if TMDB has none (or no token is set).</returns>
    public async Task<TmdbShow?> ShowAsync(string folderName, CancellationToken ct)
    {
        var node = await FetchAsync("show2:" + folderName, async () =>
        {
            var query = NameSuffix().Replace(folderName, string.Empty).Trim();
            var res = await GetAsync($"/search/tv?query={Uri.EscapeDataString(query)}&language={Languages[0]}", ct).ConfigureAwait(false);
            var r = res?["results"]?.AsArray().FirstOrDefault();
            _logger.LogInformation("TMDB: {Query} -> {Id}", query, r?["id"]?.GetValue<int>());
            return r is null ? null : new JsonObject
            {
                ["id"] = r["id"]!.GetValue<int>(),
                ["name"] = r["name"]?.GetValue<string>(),
                ["poster"] = r["poster_path"]?.GetValue<string>(),
                ["backdrop"] = r["backdrop_path"]?.GetValue<string>(),
            };
        }).ConfigureAwait(false);
        return node is null ? null : new TmdbShow(node["id"]!.GetValue<int>(), Str(node, "name"), Str(node, "poster"), Str(node, "backdrop"));
    }

    /// <summary>Picks the TMDB numbering that matches the most seasons of Portalito's (a tie: the broadcast one).</summary>
    /// <param name="tvId">TMDB series id.</param>
    /// <param name="portalSeasons">Season → episode count in Portalito.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="portalSpans">Season → highest episode number in Portalito.</param>
    /// <returns>The chosen numbering, or null if TMDB couldn't be read.</returns>
    public async Task<TmdbOrdering?> OrderingAsync(int tvId, IReadOnlyDictionary<int, int> portalSeasons, CancellationToken ct,
        IReadOnlyDictionary<int, int>? portalSpans = null)
    {
        var tv = await FetchAsync($"tv2:{tvId}:{Languages[0]}", async () =>
            await GetAsync($"/tv/{tvId}?language={Languages[0]}", ct).ConfigureAwait(false)).ConfigureAwait(false);
        if (tv is null)
        {
            return null;
        }

        var overview = tv["overview"]?.GetValue<string>();
        var candidates = new List<(string? Id, string Name, Dictionary<int, int> Counts)>
        {
            (null, "emision", (tv["seasons"] as JsonArray ?? new JsonArray())
                .Where(s => s?["season_number"]?.GetValue<int>() > 0)
                .ToDictionary(s => s!["season_number"]!.GetValue<int>(), s => s!["episode_count"]?.GetValue<int>() ?? 0)),
        };

        var groups = await FetchAsync($"groups:{tvId}", async () =>
            await GetAsync($"/tv/{tvId}/episode_groups", ct).ConfigureAwait(false)).ConfigureAwait(false);
        foreach (var g in groups?["results"] as JsonArray ?? new JsonArray())
        {
            var id = g?["id"]?.GetValue<string>();
            if (id is null)
            {
                continue;
            }

            var detail = await GroupAsync(id, Languages[0], ct).ConfigureAwait(false);
            if (detail is not null)
            {
                candidates.Add((id, g!["name"]?.GetValue<string>() ?? id,
                    detail.ToDictionary(kv => kv.Key, kv => kv.Value.Count)));
            }
        }

        var best = candidates
            .Select(c => (c.Id, c.Name, c.Counts, Trusted: TrustedSeasons(portalSeasons, portalSpans, c.Counts)))
            .OrderByDescending(c => c.Trusted.Count)
            .ThenBy(c => c.Id is null ? 0 : 1)
            .First();
        return new TmdbOrdering(best.Id, best.Name, best.Trusted, string.IsNullOrWhiteSpace(overview) ? null : overview, best.Counts);
    }

    /// <summary>An episode according to the chosen numbering (null if the season doesn't match or TMDB doesn't have it).</summary>
    /// <param name="tvId">TMDB series id.</param>
    /// <param name="ordering">The chosen numbering.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The episode, or null.</returns>
    public async Task<TmdbEpisode?> EpisodeAsync(int tvId, TmdbOrdering? ordering, int season, int episode, CancellationToken ct)
    {
        if (ordering is not null && !ordering.TrustedSeasons.Contains(season))
        {
            return null;
        }

        if (ordering?.GroupId is null)
        {
            return await EpisodeAsync(tvId, season, episode, ct).ConfigureAwait(false);
        }

        TmdbEpisode? first = null;
        foreach (var lang in Languages)
        {
            var detail = await GroupAsync(ordering.GroupId, lang, ct).ConfigureAwait(false);
            if (detail is null || !detail.TryGetValue(season, out var eps) || episode < 1 || episode > eps.Count)
            {
                continue;
            }

            var found = eps[episode - 1];
            first ??= found;
            if (!string.IsNullOrWhiteSpace(found.Name) && !GenericTitle().IsMatch(found.Name.Trim()))
            {
                return found with { Overview = string.IsNullOrWhiteSpace(found.Overview) ? first.Overview : found.Overview };
            }
        }

        return first;
    }

    /// <summary>The episode with the first non-generic name between es-MX and es-ES (broadcast numbering).</summary>
    /// <param name="tvId">TMDB series id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The episode, or null.</returns>
    public async Task<TmdbEpisode?> EpisodeAsync(int tvId, int season, int episode, CancellationToken ct)
    {
        TmdbEpisode? first = null;
        foreach (var lang in Languages)
        {
            var seasonNode = await FetchAsync($"season2:{tvId}:{season}:{lang}", async () =>
            {
                var data = await GetAsync($"/tv/{tvId}/season/{season}?language={lang}", ct).ConfigureAwait(false);
                if (data?["episodes"] is not JsonArray eps)
                {
                    return null;
                }

                var obj = new JsonObject();
                foreach (var e in eps)
                {
                    obj[e!["episode_number"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["name"] = e["name"]?.GetValue<string>() ?? string.Empty,
                        ["overview"] = e["overview"]?.GetValue<string>() ?? string.Empty,
                        ["still"] = e["still_path"]?.GetValue<string>(),
                    };
                }

                return obj;
            }).ConfigureAwait(false);

            if (seasonNode?[episode.ToString(CultureInfo.InvariantCulture)] is not JsonObject ep)
            {
                continue;
            }

            var found = new TmdbEpisode(Str(ep, "name"), Str(ep, "overview"), Str(ep, "still"));
            first ??= found;
            if (!string.IsNullOrWhiteSpace(found.Name) && !GenericTitle().IsMatch(found.Name.Trim()))
            {
                return found with { Overview = string.IsNullOrWhiteSpace(found.Overview) ? first.Overview : found.Overview };
            }
        }

        return first;
    }

    /// <summary>Looks a movie up on TMDB.</summary>
    /// <param name="title">The movie title.</param>
    /// <param name="year">The production year, if known.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The movie, or null if TMDB has none (or no token is set).</returns>
    public async Task<TmdbMovie?> MovieAsync(string title, int? year, CancellationToken ct)
    {
        var node = await FetchAsync($"movie:{title}:{year}", async () =>
        {
            var q = $"/search/movie?query={Uri.EscapeDataString(title)}&language={Languages[0]}" + (year is null ? string.Empty : $"&year={year}");
            var r = (await GetAsync(q, ct).ConfigureAwait(false))?["results"]?.AsArray().FirstOrDefault();
            if (r is null)
            {
                return null;
            }

            var image = Image(r["backdrop_path"]?.GetValue<string>(), "w780") ?? Image(r["poster_path"]?.GetValue<string>(), "w500");
            return new JsonObject { ["id"] = r["id"]!.GetValue<int>(), ["overview"] = r["overview"]?.GetValue<string>(), ["image"] = image };
        }).ConfigureAwait(false);
        return node is null ? null : new TmdbMovie(node["id"]!.GetValue<int>(), Str(node, "overview"), Str(node, "image"));
    }

    /// <summary>Writes the cache to disk if it changed.</summary>
    public void SaveIfDirty()
    {
        Dictionary<string, JsonNode?> copy;
        lock (_lock)
        {
            if (!_dirty || _cache is null)
            {
                return;
            }

            copy = _cache.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone());
            _dirty = false;
        }

        JsonFile.Save(FilePath, copy);
    }

    // "Episodio 3", "Episode 3", "Capítulo 3" and Portalito's fillers: "<Show> Season 27 - 1", "Temporada 2 - 5".
    [GeneratedRegex(@"^((episodio|episode|cap[ií]tulo)\s*\d+|.*\b(season|temporada)\s*\d+\s*-\s*\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTitle();

    [GeneratedRegex(@"\s+-\s+.*$")]
    private static partial Regex NameSuffix();

    private static string? Str(JsonNode node, string name) => node[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>An episode group: season (the group's order) → episodes in order.</summary>
    private async Task<Dictionary<int, List<TmdbEpisode>>?> GroupAsync(string groupId, string lang, CancellationToken ct)
    {
        var node = await FetchAsync($"group2:{groupId}:{lang}", async () =>
        {
            var data = await GetAsync($"/tv/episode_group/{groupId}?language={lang}", ct).ConfigureAwait(false);
            if (data?["groups"] is not JsonArray groups)
            {
                return null;
            }

            // Stored compactly: { "<order>": [ {name, overview, still}, ... ] }
            var obj = new JsonObject();
            foreach (var g in groups)
            {
                var arr = new JsonArray();
                foreach (var e in (g!["episodes"] as JsonArray ?? new JsonArray()).OrderBy(e => e?["order"]?.GetValue<int>() ?? 0))
                {
                    arr.Add(new JsonObject
                    {
                        ["name"] = e!["name"]?.GetValue<string>() ?? string.Empty,
                        ["overview"] = e["overview"]?.GetValue<string>() ?? string.Empty,
                        ["still"] = e["still_path"]?.GetValue<string>(),
                    });
                }

                obj[(g["order"]?.GetValue<int>() ?? 0).ToString(CultureInfo.InvariantCulture)] = arr;
            }

            return obj;
        }).ConfigureAwait(false);
        if (node is not JsonObject o)
        {
            return null;
        }

        return o.ToDictionary(
            kv => int.Parse(kv.Key, CultureInfo.InvariantCulture),
            kv => (kv.Value as JsonArray ?? new JsonArray())
                .Select(e => new TmdbEpisode(Str(e!, "name"), Str(e!, "overview"), Str(e!, "still"))).ToList());
    }

    private async Task<JsonNode?> FetchAsync(string key, Func<Task<JsonNode?>> fetch)
    {
        var cache = Cache;
        lock (_lock)
        {
            if (cache.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }

        if (string.IsNullOrEmpty(Plugin.Instance!.Configuration.TmdbToken) || DateTime.UtcNow < _downUntil)
        {
            return null;
        }

        JsonNode? value;
        try
        {
            value = await fetch().ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            value = null; // TMDB doesn't have that season: it is remembered as empty
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _downUntil = DateTime.UtcNow.AddMinutes(10);
            _logger.LogWarning("TMDB unavailable ({Error}); going on without new data", e.GetType().Name);
            return null;
        }

        lock (_lock)
        {
            cache[key] = value;
            _dirty = true;
        }

        return value;
    }

    private async Task<JsonNode?> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3" + pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Plugin.Instance!.Configuration.TmdbToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
