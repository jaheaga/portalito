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

    private readonly HttpClient _http;
    private readonly PortalOptions _options;

    public SlbDiscovery(HttpClient http, PortalOptions options)
    {
        _http = http;
        _options = options;
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

        foreach (var seed in _options.Hosts)
        {
            try
            {
                var text = await PostAsync(seed, envelope, cancellationToken).ConfigureAwait(false);
                var hosts = ParsePortalHosts(text, _options.DiscoveryAesKey, _options.DiscoveryService);
                if (hosts.Count > 0)
                {
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
