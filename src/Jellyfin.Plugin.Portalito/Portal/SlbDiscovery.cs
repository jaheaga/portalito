using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Portalito.Portal;

/// <summary>
/// SLB (server load-balancer) host discovery. Some portals don't expose their API hosts directly: the app first asks a
/// seed host for the live host list, encrypted with a separate AES key. When <see cref="PortalOptions.DiscoveryEnabled"/>
/// is set, the plugin runs this against the configured <see cref="PortalOptions.Hosts"/> (the seeds) and calls the hosts it
/// returns. All values are configuration; nothing here is specific to any one portal.
/// </summary>
public sealed class SlbDiscovery : IHostDiscovery
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How many learned bootstrap seeds to keep: enough redundancy to survive a rotation, few enough that a long
    /// list of stale hosts can't build up (newest first, so older ones age out past this cap).</summary>
    internal const int MaxPersistedSeeds = 8;

    private readonly HttpClient _http;
    private readonly PortalOptions _options;
    private readonly Action<IReadOnlyList<string>>? _onSeedsDiscovered;

    /// <param name="onSeedsDiscovered">
    /// Called with the refreshed bootstrap seeds (newest first) when <see cref="PortalOptions.PersistDiscoveredSeeds"/> is
    /// on and a discovery returns a seed set that differs from the one in <paramref name="options"/>, so the host can
    /// persist them for the next startup. Optional.
    /// </param>
    public SlbDiscovery(HttpClient http, PortalOptions options, Action<IReadOnlyList<string>>? onSeedsDiscovered = null)
    {
        _http = http;
        _options = options;
        _onSeedsDiscovered = onSeedsDiscovered;
    }

    /// <summary>The hosts to try as discovery bootstrap seeds: the configured hosts first, then any the plugin has learned.</summary>
    private IEnumerable<string> BootstrapSeeds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in _options.Hosts.Concat(_options.DiscoverySeeds))
        {
            if (seen.Add(host))
            {
                yield return host;
            }
        }
    }

    /// <summary>
    /// Resolves the live hosts for the configured service, trying each seed in turn; returns an empty list if discovery is
    /// off or every seed fails (the caller then uses the seeds directly).
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolvePortalHostsAsync(CancellationToken cancellationToken)
    {
        if (!_options.DiscoveryEnabled)
        {
            return Array.Empty<string>();
        }

        string envelope;
        try
        {
            var wire = EncryptPayload(BuildRequestBody(_options), _options.DiscoveryAesKey);
            envelope = new JsonObject { ["s"] = wire, ["status"] = 0 }.ToJsonString();
        }
        catch
        {
            // A bad key (e.g. not 16/24/32 bytes) can't be encrypted with: disable discovery for this round so the
            // caller falls back to the seeds, instead of throwing out of every portal call.
            return Array.Empty<string>();
        }

        foreach (var seed in BootstrapSeeds())
        {
            try
            {
                var text = await PostAsync(seed, envelope, cancellationToken).ConfigureAwait(false);
                var hosts = ParsePortalHosts(text, _options.DiscoveryAesKey, _options.DiscoveryService);
                if (hosts.Count > 0)
                {
                    MaybePersistSeeds(text);
                    return hosts;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Seed unreachable or unparseable: try the next one, then fall back to the seeds directly.
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// When seed persistence is on, records the current bootstrap-seed hosts (the <see cref="PortalOptions.DiscoverySeedService"/>
    /// entries of the same directory) so discovery can still start after the configured hosts are retired. Fresh hosts
    /// go first and the list is capped at <see cref="MaxPersistedSeeds"/>, so stale ones age out; the host is told only
    /// when the set actually changes, to avoid needless configuration writes.
    /// </summary>
    private void MaybePersistSeeds(string responseText)
    {
        if (!_options.PersistDiscoveredSeeds || _onSeedsDiscovered is null)
        {
            return;
        }

        var fresh = ParsePortalHosts(responseText, _options.DiscoveryAesKey, _options.DiscoverySeedService);
        if (fresh.Count == 0)
        {
            return;
        }

        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in fresh.Concat(_options.DiscoverySeeds))
        {
            if (seen.Add(host) && merged.Count < MaxPersistedSeeds)
            {
                merged.Add(host);
            }
        }

        if (!merged.SequenceEqual(_options.DiscoverySeeds, StringComparer.Ordinal))
        {
            _onSeedsDiscovered(merged);
        }
    }

    private async Task<string> PostAsync(string seed, string envelope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{seed}{_options.DiscoveryPath}");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(envelope));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("ETag", string.Empty);
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", _options.PortalUserAgent);

        // Sign the discovery call too, in case the portal enforces the signature on every endpoint (no-op when no secret).
        var (trace, meta) = HttpPortalTransport.SignatureHeaders(_options.RequestSignSecret, Guid.NewGuid().ToString());
        if (trace is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Request-Trace", trace);
            request.Headers.TryAddWithoutValidation("X-Client-Meta", meta);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>The discovery request body: the device/app identity the seed expects, before AES wrapping.</summary>
    internal static string BuildRequestBody(PortalOptions o)
    {
        var mac = o.DeviceReserve1.Length > 0 ? o.DeviceReserve1 : "02:00:00:00:00:00";
        return new JsonObject
        {
            ["entry_type"] = "all",
            ["sn"] = o.DeviceSn,
            ["code"] = string.Empty,
            ["auth_version"] = string.Empty,
            ["approve_code"] = string.Empty,
            ["open_num"] = null,
            ["apk"] = o.AppId,
            ["apk_ver"] = o.ApkVersion,
            // A short OS-version placeholder; the discovery service does not validate it (verified against a free device).
            ["spkg_ver"] = "16",
            ["reserve1"] = mac,
            ["type"] = 0,
            ["user_id"] = string.Empty,
            ["user_identity"] = string.Empty,
        }.ToJsonString();
    }

    /// <summary>AES/CBC/PKCS7 with a fresh random IV prepended, base64-encoded (matches the reference discovery client).</summary>
    internal static string EncryptPayload(string plaintext, string asciiKey)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(asciiKey);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();
        var iv = aes.IV;
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), iv, PaddingMode.PKCS7);
        var combined = new byte[iv.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
        Buffer.BlockCopy(cipher, 0, combined, iv.Length, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    /// <summary>Reverses <see cref="EncryptPayload"/>: base64 decode, split off the 16-byte IV, AES/CBC decrypt.</summary>
    internal static string DecryptPayload(string base64, string asciiKey)
    {
        var raw = Convert.FromBase64String(base64);
        var iv = raw[..16];
        var cipher = raw[16..];
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(asciiKey);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return Encoding.UTF8.GetString(aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7));
    }

    /// <summary>
    /// Extracts the hosts for <paramref name="service"/> from a discovery response. The response is
    /// <c>{"log": AES(services)}</c>, where services is <c>{scope: [{business_entry, details:[{url}]}]}</c>; group-scoped
    /// hosts (account-specific) come before global ones, deduplicated.
    /// </summary>
    internal static IReadOnlyList<string> ParsePortalHosts(string responseText, string asciiKey, string service)
    {
        if (JsonNode.Parse(responseText) is not JsonObject envelope
            || envelope["log"] is not JsonValue logValue
            || !logValue.TryGetValue<string>(out var log)
            || log.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (JsonNode.Parse(DecryptPayload(log, asciiKey)) is not JsonObject services)
        {
            return Array.Empty<string>();
        }

        var hosts = new List<string>();

        void Collect(string scope)
        {
            if (services[scope] is not JsonArray entries)
            {
                return;
            }

            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (!string.Equals((entry["business_entry"] as JsonValue)?.GetValue<string>(), service, StringComparison.Ordinal)
                    || entry["details"] is not JsonArray details)
                {
                    continue;
                }

                foreach (var detail in details.OfType<JsonObject>())
                {
                    if ((detail["url"] as JsonValue)?.GetValue<string>() is not { Length: > 0 } raw)
                    {
                        continue;
                    }

                    // Normalize exactly like the configured seeds: a discovered value may carry a scheme or path.
                    var host = PortalOptions.StripSchemeAndPath(raw);
                    if (host.Length > 0 && !hosts.Contains(host))
                    {
                        hosts.Add(host);
                    }
                }
            }
        }

        // Group scope first: these are the account's own routing, which the reference app prefers.
        Collect("group");
        Collect("global");
        return hosts;
    }
}
