using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Xtream;

namespace Jellyfin.Plugin.Portalito.Metadata;

/// <summary>
/// Fetches the community IPTV channel database (a single public JSON file) once: the body text, or null when it's a
/// 404. Any other failure (5xx, no network) throws <see cref="HttpRequestException"/>, so an outage is never cached as
/// "no logos". Testable without a network.
/// </summary>
public interface ICommunityLogoTransport
{
    Task<string?> GetChannelsJsonAsync(CancellationToken cancellationToken);
}

/// <summary>HttpClient-backed <see cref="ICommunityLogoTransport"/> against the public iptv-org channels database.</summary>
public sealed class HttpCommunityLogoTransport : ICommunityLogoTransport
{
    private const string ChannelsUrl = "https://iptv-org.github.io/api/channels.json";

    private readonly HttpClient _http;

    public HttpCommunityLogoTransport(HttpClient http) => _http = http;

    public async Task<string?> GetChannelsJsonAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(ChannelsUrl), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : throw new HttpRequestException($"Community logo set answered {(int)response.StatusCode}", null, response.StatusCode);
    }
}

/// <summary>
/// Finds a logo for a live channel that the IPTV panel shipped without one, by matching the channel's name against the
/// community IPTV channel database (its <c>name</c>/<c>alt_names</c> -&gt; <c>logo</c> URL). Matching is a strict
/// normalized-equality test (<see cref="XtreamCatalog.NormalizeTitle"/>), never fuzzy, so a wrong logo isn't shown. The
/// whole database is fetched and indexed once, cached in memory for a week (it changes slowly); a concurrent burst
/// shares one fetch (<see cref="SingleFlight"/>). Durable per-channel results are the caller's job
/// (<see cref="ChannelLogoStore"/>); this is only the lookup.
/// </summary>
public sealed class CommunityLogoResolver
{
    private const string IndexKey = "community-logos";

    private static readonly IReadOnlyDictionary<string, string> EmptyIndex =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly ICommunityLogoTransport _transport;
    private readonly SingleFlight _flights = new();
    private readonly TtlCache<IReadOnlyDictionary<string, string>> _index;

    public CommunityLogoResolver(ICommunityLogoTransport transport, TimeProvider clock)
    {
        _transport = transport;
        _index = new TtlCache<IReadOnlyDictionary<string, string>>(clock, TimeSpan.FromDays(7), capacity: 1);
    }

    /// <summary>The logo URL for a channel name, or <c>null</c> when the community set has no entry under that name.</summary>
    public async Task<string?> FindLogoAsync(string channelName, CancellationToken cancellationToken)
    {
        var key = XtreamCatalog.NormalizeTitle(channelName);
        if (key.Length == 0)
        {
            return null;
        }

        var index = await IndexAsync(cancellationToken).ConfigureAwait(false);
        return index.TryGetValue(key, out var url) ? url : null;
    }

    /// <summary>Builds the normalized-name -&gt; logo-URL index from the channels database JSON (the testable core).</summary>
    internal static IReadOnlyDictionary<string, string> BuildIndex(string json)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonNode.Parse(json) is not JsonArray channels)
        {
            return index;
        }

        foreach (var entry in channels)
        {
            if (entry is not JsonObject channel)
            {
                continue;
            }

            var logo = channel["logo"]?.ToString();
            if (string.IsNullOrWhiteSpace(logo)
                || !Uri.TryCreate(logo, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
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

    private static void Add(Dictionary<string, string> index, string? name, string logo)
    {
        if (name is null)
        {
            return;
        }

        var key = XtreamCatalog.NormalizeTitle(name);
        if (key.Length > 0)
        {
            // First entry for a name wins, so the index is deterministic regardless of JSON order within a build.
            index.TryAdd(key, logo);
        }
    }

    private Task<IReadOnlyDictionary<string, string>> IndexAsync(CancellationToken cancellationToken)
    {
        if (_index.TryGet(IndexKey, out var cached))
        {
            return Task.FromResult(cached);
        }

        return _flights.RunAsync(IndexKey, async () =>
        {
            var json = await _transport.GetChannelsJsonAsync(cancellationToken).ConfigureAwait(false);
            var built = json is null ? EmptyIndex : BuildIndex(json);
            _index.Set(IndexKey, built);
            return built;
        }, cancellationToken);
    }
}
