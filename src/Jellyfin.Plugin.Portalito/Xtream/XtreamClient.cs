using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Portalito.Configuration;

namespace Jellyfin.Plugin.Portalito.Xtream;

/// <summary>
/// A thin client for an Xtream Codes IPTV panel (the secondary media source). Talks the panel's <c>player_api.php</c>
/// and builds the direct stream URLs. Unlike the portal client there is no encryption, session or version gate: the
/// username and password sit in the URL and the server answers plain JSON. Nothing here is service-specific -- the base
/// URL, username and password all come from <see cref="XtreamOptions"/> (the plugin configuration).
/// </summary>
public sealed class XtreamClient
{
    private readonly HttpClient _http;
    private readonly XtreamOptions _options;

    public XtreamClient(HttpClient http, XtreamOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>Gets the configured base URL (scheme, host and port), without a trailing slash.</summary>
    public string BaseUrl => _options.BaseUrl;

    /// <summary>
    /// Reads the account info (<c>player_api.php</c> with no action). Throws <see cref="XtreamException"/> when the
    /// server is unreachable, the URL is wrong, or the credentials are rejected -- the health check the config page and
    /// the source-health task use.
    /// </summary>
    public async Task<XtreamAuth> AuthAsync(CancellationToken cancellationToken)
    {
        var node = await GetJsonAsync(Api(null), cancellationToken).ConfigureAwait(false);
        if (node is not JsonObject root || root["user_info"] is not JsonObject info)
        {
            throw new XtreamException("The IPTV server didn't return account info (wrong URL or credentials?).");
        }

        if ((Int(info["auth"]) ?? 0) != 1)
        {
            throw new XtreamException("The IPTV server rejected the username or password.");
        }

        return new XtreamAuth(Str(info["status"]) ?? string.Empty, Int(info["max_connections"]) ?? 0, Int(info["active_cons"]) ?? 0);
    }

    /// <summary>Lists the VOD (movie) categories.</summary>
    public Task<IReadOnlyList<XtreamCategory>> GetVodCategoriesAsync(CancellationToken cancellationToken)
        => CategoriesAsync("get_vod_categories", cancellationToken);

    /// <summary>Lists the series categories.</summary>
    public Task<IReadOnlyList<XtreamCategory>> GetSeriesCategoriesAsync(CancellationToken cancellationToken)
        => CategoriesAsync("get_series_categories", cancellationToken);

    /// <summary>
    /// Lists every VOD stream (id, name, container extension). The bulk list carries no TMDB id -- that is only in
    /// <see cref="GetVodInfoAsync"/> -- so movie matching is by title + year, with a lazy TMDB confirmation.
    /// </summary>
    public async Task<IReadOnlyList<XtreamVodItem>> GetVodStreamsAsync(CancellationToken cancellationToken)
    {
        var array = await GetArrayAsync(Api("get_vod_streams"), cancellationToken).ConfigureAwait(false);
        var items = new List<XtreamVodItem>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject o || Int(o["stream_id"]) is not { } id)
            {
                continue;
            }

            items.Add(new XtreamVodItem(id, Str(o["name"]) ?? string.Empty, Str(o["container_extension"]), Str(o["category_id"]) ?? string.Empty));
        }

        return items;
    }

    /// <summary>Reads one movie's detail (for the TMDB id and the real container extension).</summary>
    public async Task<XtreamVodInfo?> GetVodInfoAsync(int vodId, CancellationToken cancellationToken)
    {
        var node = await GetJsonAsync(Api("get_vod_info", ("vod_id", vodId.ToString(CultureInfo.InvariantCulture))), cancellationToken).ConfigureAwait(false);
        if (node is not JsonObject root)
        {
            return null;
        }

        var info = root["info"] as JsonObject;
        var movie = root["movie_data"] as JsonObject;
        var tmdb = Int(info?["tmdb_id"]);
        var ext = Str(movie?["container_extension"]);
        if (tmdb is null && ext is null && info is null)
        {
            return null;
        }

        return new XtreamVodInfo(tmdb, ext);
    }

    /// <summary>Lists every series (id, name, TMDB id, year) -- the source of the <c>tmdb_id -&gt; series_id</c> map.</summary>
    public async Task<IReadOnlyList<XtreamSeriesItem>> GetSeriesAsync(CancellationToken cancellationToken)
    {
        var array = await GetArrayAsync(Api("get_series"), cancellationToken).ConfigureAwait(false);
        var items = new List<XtreamSeriesItem>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject o || Int(o["series_id"]) is not { } id)
            {
                continue;
            }

            var name = Str(o["name"]) ?? string.Empty;
            items.Add(new XtreamSeriesItem(id, name, Int(o["tmdb_id"]), Str(o["category_id"]) ?? string.Empty, YearOf(Str(o["releaseDate"]), name)));
        }

        return items;
    }

    /// <summary>Reads a series' seasons and episodes (each episode's season, number, id and container extension).</summary>
    public async Task<XtreamSeriesInfo> GetSeriesInfoAsync(int seriesId, CancellationToken cancellationToken)
    {
        var node = await GetJsonAsync(Api("get_series_info", ("series_id", seriesId.ToString(CultureInfo.InvariantCulture))), cancellationToken).ConfigureAwait(false);
        var episodes = new List<XtreamEpisode>();
        int? tmdb = null;
        if (node is JsonObject root)
        {
            tmdb = Int((root["info"] as JsonObject)?["tmdb_id"]);

            // "episodes" is a map of season-number -> array of episode objects.
            if (root["episodes"] is JsonObject seasons)
            {
                foreach (var (_, seasonEpisodes) in seasons)
                {
                    if (seasonEpisodes is not JsonArray list)
                    {
                        continue;
                    }

                    foreach (var entry in list)
                    {
                        if (entry is not JsonObject e || Str(e["id"]) is not { } id)
                        {
                            continue;
                        }

                        var info = e["info"] as JsonObject;
                        episodes.Add(new XtreamEpisode(
                            id,
                            Int(e["season"]) ?? 0,
                            Int(e["episode_num"]) ?? 0,
                            Str(e["container_extension"]),
                            Int(info?["tmdb_id"]),
                            Str(e["title"]) ?? string.Empty));
                    }
                }
            }
        }

        return new XtreamSeriesInfo(tmdb, episodes);
    }

    /// <summary>Builds the direct URL for a movie stream.</summary>
    public Uri MovieUrl(int streamId, string? containerExtension)
        => Content("movie", streamId.ToString(CultureInfo.InvariantCulture), containerExtension);

    /// <summary>Builds the direct URL for a series episode (the episode id from <see cref="GetSeriesInfoAsync"/>).</summary>
    public Uri SeriesEpisodeUrl(string episodeId, string? containerExtension)
        => Content("series", episodeId, containerExtension);

    /// <summary>Builds the direct URL for a live channel stream.</summary>
    public Uri LiveUrl(int streamId, string? containerExtension = null)
        => Content("live", streamId.ToString(CultureInfo.InvariantCulture), containerExtension ?? "ts");

    private async Task<IReadOnlyList<XtreamCategory>> CategoriesAsync(string action, CancellationToken cancellationToken)
    {
        var array = await GetArrayAsync(Api(action), cancellationToken).ConfigureAwait(false);
        var items = new List<XtreamCategory>(array.Count);
        foreach (var entry in array)
        {
            if (entry is JsonObject o && Str(o["category_id"]) is { } id)
            {
                items.Add(new XtreamCategory(id, Str(o["category_name"]) ?? string.Empty));
            }
        }

        return items;
    }

    private Uri Content(string kind, string id, string? containerExtension)
    {
        var ext = string.IsNullOrWhiteSpace(containerExtension) ? "mp4" : containerExtension.TrimStart('.');
        var url = $"{_options.BaseUrl}/{kind}/{Uri.EscapeDataString(_options.Username)}/{Uri.EscapeDataString(_options.Password)}/{id}.{ext}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new XtreamException($"The IPTV base URL '{_options.BaseUrl}' isn't an http(s) address like http://host:port.");
        }

        return uri;
    }

    private Uri Api(string? action, params (string Key, string Value)[] extra)
    {
        var sb = new StringBuilder(_options.BaseUrl.Length + 64);
        sb.Append(_options.BaseUrl)
          .Append("/player_api.php?username=").Append(Uri.EscapeDataString(_options.Username))
          .Append("&password=").Append(Uri.EscapeDataString(_options.Password));
        if (action is not null)
        {
            sb.Append("&action=").Append(Uri.EscapeDataString(action));
        }

        foreach (var (key, value) in extra)
        {
            sb.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        if (!Uri.TryCreate(sb.ToString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new XtreamException($"The IPTV base URL '{_options.BaseUrl}' isn't an http(s) address like http://host:port.");
        }

        return uri;
    }

    private async Task<JsonArray> GetArrayAsync(Uri uri, CancellationToken cancellationToken)
    {
        // Xtream answers a request with no results as the JSON literal "false" or an empty string, not [].
        var node = await GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        return node as JsonArray ?? new JsonArray();
    }

    private async Task<JsonNode?> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new XtreamException($"The IPTV server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new XtreamException("The IPTV server's response wasn't valid JSON (wrong URL?).", ex);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new XtreamException($"The IPTV server can't be reached ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XtreamException("The IPTV server didn't answer in time.", ex);
        }
    }

    /// <summary>Reads a JSON value that may be a string or a number as a string (Xtream mixes the two freely).</summary>
    internal static string? Str(JsonNode? node) => node?.ToString();

    /// <summary>Reads a JSON value that may be a string or a number as an int, or <c>null</c> when it is neither.</summary>
    internal static int? Int(JsonNode? node)
        => int.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>The release year, from an ISO date (<c>2026-04-27</c>) or a trailing <c>(2026)</c> in the title.</summary>
    internal static int? YearOf(string? releaseDate, string? name)
    {
        if (releaseDate is { Length: >= 4 } && int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is > 1800 and < 2200)
        {
            return year;
        }

        var match = Regex.Match(name ?? string.Empty, @"\((19|20)\d{2}\)");
        return match.Success && int.TryParse(match.Value.AsSpan(1, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
    }
}
