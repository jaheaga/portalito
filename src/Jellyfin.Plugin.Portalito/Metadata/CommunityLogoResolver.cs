using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;

namespace Jellyfin.Plugin.Portalito.Metadata;

/// <summary>
/// Fetches the two public files of the community IPTV channel database -- the channel list (names -&gt; id) and the logo
/// list (id -&gt; image URL) -- as text, or null when a file is a 404. Any other failure throws
/// <see cref="HttpRequestException"/>, so an outage is never cached as "no logos". Testable without a network.
/// </summary>
public interface ICommunityLogoTransport
{
    Task<string?> GetChannelsJsonAsync(CancellationToken cancellationToken);

    Task<string?> GetLogosJsonAsync(CancellationToken cancellationToken);
}

/// <summary>HttpClient-backed <see cref="ICommunityLogoTransport"/> against the public iptv-org database files.</summary>
public sealed class HttpCommunityLogoTransport : ICommunityLogoTransport
{
    private const string ChannelsUrl = "https://iptv-org.github.io/api/channels.json";
    private const string LogosUrl = "https://iptv-org.github.io/api/logos.json";

    private readonly HttpClient _http;

    public HttpCommunityLogoTransport(HttpClient http) => _http = http;

    public Task<string?> GetChannelsJsonAsync(CancellationToken cancellationToken) => GetAsync(ChannelsUrl, cancellationToken);

    public Task<string?> GetLogosJsonAsync(CancellationToken cancellationToken) => GetAsync(LogosUrl, cancellationToken);

    private async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : throw new HttpRequestException($"Community logo set answered {(int)response.StatusCode} for {url}", null, response.StatusCode);
    }
}

/// <summary>
/// Finds a logo for a live channel the IPTV panel shipped without one, by matching the channel's name against the
/// community IPTV channel database (iptv-org): its <c>channels.json</c> gives name/alt_names -&gt; channel id, and its
/// <c>logos.json</c> gives id -&gt; image URL (they are two separate files, joined here on the channel id). Panel channel
/// names are heavily decorated -- a country/provider prefix (<c>AR |</c>), a quality suffix (<c>HD</c>/<c>SD</c>/<c>FHD</c>),
/// unicode look-alikes (<c>C∆N∆L</c>, small-caps <c>ʜᴅ</c>) -- so matching strips those before an exact
/// normalized-equality test (never fuzzy, to avoid a wrong logo). Both files are fetched and indexed once, cached in
/// memory for a week; a concurrent burst shares one build (<see cref="SingleFlight"/>). Durable per-channel results are
/// the caller's job (<see cref="ChannelLogoStore"/>).
/// </summary>
public sealed class CommunityLogoResolver
{
    private const string IndexKey = "community-logos";

    private static readonly IReadOnlyDictionary<string, string> EmptyIndex =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Latin small-capital / math look-alike letters some panels use to dodge name filters, mapped back to ASCII.
    private static readonly IReadOnlyDictionary<char, char> Confusables = new Dictionary<char, char>
    {
        ['ᴀ'] = 'a', ['ʙ'] = 'b', ['ᴄ'] = 'c', ['ᴅ'] = 'd', ['ᴇ'] = 'e', ['ꜰ'] = 'f', ['ɢ'] = 'g', ['ʜ'] = 'h',
        ['ɪ'] = 'i', ['ᴊ'] = 'j', ['ᴋ'] = 'k', ['ʟ'] = 'l', ['ᴍ'] = 'm', ['ɴ'] = 'n', ['ᴏ'] = 'o', ['ᴘ'] = 'p',
        ['ꞯ'] = 'q', ['ʀ'] = 'r', ['ꜱ'] = 's', ['ᴛ'] = 't', ['ᴜ'] = 'u', ['ᴠ'] = 'v', ['ʏ'] = 'y',
        ['∆'] = 'a', ['Δ'] = 'a', ['$'] = 's', ['€'] = 'e', ['¢'] = 'c',
    };

    // Leading tokens to drop: a country/provider code (AR, CO, MX...) or a bare separator (the "l"/"i" in "AR l", "AR i|").
    private static readonly HashSet<string> Separators = new(StringComparer.Ordinal) { "l", "i", "ll", "il" };

    private static readonly HashSet<string> CountryCodes = new(StringComparer.Ordinal)
    {
        "ar", "bo", "br", "ca", "cl", "co", "cr", "do", "ec", "es", "gt", "hn", "mx", "ni", "pa", "pe", "pr", "py", "sv", "us", "uy", "ve",
    };

    // Quality / variant tags to drop anywhere in the name.
    private static readonly HashSet<string> QualityTags = new(StringComparer.Ordinal)
    {
        "hd", "sd", "fhd", "uhd", "4k", "fullhd", "hevc", "h265", "h264", "opc", "op", "alt", "backup",
    };

    private readonly ICommunityLogoTransport _transport;
    private readonly SingleFlight _flights = new();
    private readonly TtlCache<IReadOnlyDictionary<string, string>> _index;

    public CommunityLogoResolver(ICommunityLogoTransport transport, TimeProvider clock)
    {
        _transport = transport;
        _index = new TtlCache<IReadOnlyDictionary<string, string>>(clock, TimeSpan.FromDays(7), capacity: 1);
    }

    /// <summary>The logo URL for a channel name, or <c>null</c> when the community set has nothing under that name.</summary>
    public async Task<string?> FindLogoAsync(string channelName, CancellationToken cancellationToken)
    {
        var key = NormalizeChannelName(channelName);
        if (key.Length == 0)
        {
            return null;
        }

        // The one-time database build runs to completion on its own (shared and cached); this caller only waits for it
        // within its own budget, so a slow first download (the two files are ~18 MB) isn't cancelled and lost.
        var index = await IndexAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return index.TryGetValue(key, out var url) ? url : null;
    }

    /// <summary>
    /// Normalizes a channel name for matching: maps unicode look-alikes to ASCII, strips accents, drops a leading
    /// country/provider prefix and any quality tags, and reduces to single-spaced lowercase alphanumeric words.
    /// "AR | C∆N∆L 13 ʜᴅ" and "Canal 13" both become "canal 13". The index and the query are normalized the same way.
    /// </summary>
    internal static string NormalizeChannelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var mapped = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            mapped.Append(Confusables.TryGetValue(ch, out var ascii) ? ascii : ch);
        }

        // FormKD also folds full-width letters and superscripts (¹ -> 1) to their plain forms.
        var decomposed = mapped.ToString().Normalize(NormalizationForm.FormKD);
        var cleaned = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            cleaned.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        var tokens = cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Drop a leading provider/country prefix and its separator ("AR |", "AR l", "AR i|"); keep at least one token.
        while (tokens.Count > 1 && (Separators.Contains(tokens[0]) || CountryCodes.Contains(tokens[0])))
        {
            tokens.RemoveAt(0);
        }

        // Drop quality tags and lone-digit tokens (a channel number like "13" is two chars, so it's kept).
        tokens = tokens.Where(t => !QualityTags.Contains(t) && !(t.Length == 1 && char.IsDigit(t[0]))).ToList();
        return string.Join(' ', tokens);
    }

    /// <summary>Joins the channel list and the logo list into a normalized-name -&gt; logo-URL index (the testable core).</summary>
    internal static IReadOnlyDictionary<string, string> BuildIndex(string channelsJson, string logosJson)
    {
        var bestLogo = BuildLogoMap(logosJson);
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonNode.Parse(channelsJson) is not JsonArray channels)
        {
            return index;
        }

        foreach (var entry in channels)
        {
            if (entry is not JsonObject channel
                || channel["id"]?.ToString() is not { } id
                || !bestLogo.TryGetValue(id, out var logo))
            {
                continue;
            }

            Add(index, channel["name"]?.ToString(), logo);
            if (channel["alt_names"] is JsonArray altNames)
            {
                foreach (var alt in altNames)
                {
                    Add(index, alt?.ToString(), logo);
                }
            }
        }

        return index;
    }

    /// <summary>channel id -&gt; its best logo URL (prefer one marked in use, then the largest), from <c>logos.json</c>.</summary>
    private static Dictionary<string, string> BuildLogoMap(string logosJson)
    {
        var best = new Dictionary<string, (long Score, string Url)>(StringComparer.Ordinal);
        if (JsonNode.Parse(logosJson) is not JsonArray logos)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (var entry in logos)
        {
            if (entry is not JsonObject logo
                || logo["channel"]?.ToString() is not { } id
                || logo["url"]?.ToString() is not { } url
                || string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var inUse = logo["in_use"]?.GetValue<bool>() ?? false;
            var area = (long)(Int(logo["width"]) ?? 0) * (Int(logo["height"]) ?? 0);
            var score = (inUse ? 1_000_000_000L : 0L) + area;
            if (!best.TryGetValue(id, out var current) || score > current.Score)
            {
                best[id] = (score, url);
            }
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Url, StringComparer.Ordinal);
    }

    private static int? Int(JsonNode? node)
        => int.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static void Add(Dictionary<string, string> index, string? name, string logo)
    {
        var key = NormalizeChannelName(name);
        if (key.Length > 0)
        {
            // First entry for a name wins, so the index is deterministic regardless of order within a build.
            index.TryAdd(key, logo);
        }
    }

    private Task<IReadOnlyDictionary<string, string>> IndexAsync()
    {
        if (_index.TryGet(IndexKey, out var cached))
        {
            return Task.FromResult(cached);
        }

        // Built with no caller's cancellation token: the download must finish and cache once, independently of whether
        // any one listing that triggered it has given up waiting (see FindLogoAsync).
        return _flights.RunAsync(IndexKey, async () =>
        {
            var channelsTask = _transport.GetChannelsJsonAsync(CancellationToken.None);
            var logosJson = await _transport.GetLogosJsonAsync(CancellationToken.None).ConfigureAwait(false);
            var channelsJson = await channelsTask.ConfigureAwait(false);
            var built = channelsJson is null || logosJson is null ? EmptyIndex : BuildIndex(channelsJson, logosJson);
            _index.Set(IndexKey, built);
            return built;
        }, CancellationToken.None);
    }
}
