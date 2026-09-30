using System.Security.Cryptography;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Configuration;
using Jellyfin.Plugin.Portalito.Portal;
using Jellyfin.Plugin.Portalito.Proxy;

namespace Jellyfin.Plugin.Portalito;

/// <summary>The live object graph for the current plugin configuration.</summary>
public sealed record PortalitoServices(
    PortalClient Portal,
    ProxyUrlSigner Signer,
    PortalitoProxyService Proxy,
    CatalogBrowser Catalogs,
    Metadata.TmdbClient? Tmdb = null,
    TimeZoneInfo? EpgTimeZone = null,
    string? EpgTimeZoneProblem = null,
    DiscoveryBrowser? Discovery = null);

public interface IPortalitoServicesProvider
{
    /// <summary>Returns the services for the current configuration; throws <see cref="PortalException"/> if it is incomplete.</summary>
    PortalitoServices Get();

    /// <summary>
    /// A short value that changes every time the configuration changes (and on every Jellyfin start). The channel hands it
    /// to Jellyfin as its cache key, so a saved setting shows up in the next listing instead of after Jellyfin's 3-hour
    /// channel cache expires. Never throws.
    /// </summary>
    string CacheStamp() => string.Empty;
}

/// <summary>
/// Builds <see cref="PortalitoServices"/> from the plugin configuration and rebuilds them when the configuration changes, so
/// saving the config page takes effect without restarting Jellyfin. Also mints the proxy signing secret on first use.
/// </summary>
public sealed class PortalitoRuntime : IPortalitoServicesProvider
{
    private readonly Func<PluginConfiguration> _getConfig;
    private readonly Action _saveConfig;
    private readonly TimeProvider _clock;
    private readonly Func<string>? _localBaseUrl;
    private readonly object _gate = new();

    // Created once and shared by every rebuild: none of them depends on the configuration (the portal's one only on
    // the TLS switch, so there are at most two). Rebuilding them on every config save leaked a connection pool per
    // save, and disposing the old ones instead would cut off a VOD stream still reading through them.
    // ConnectTimeout (the reference client measured cold channels waiting ~13s on a dead CDN address) bounds just the TCP/TLS
    // handshake, so a dead CDN fails fast and the live proxy fails over. Timeout is infinite, not a fixed duration:
    // this client streams whole VOD files (900MB+, easily minutes to transfer) through OpenVodAsync, and
    // HttpClient.Timeout bounds the ENTIRE request INCLUDING body-read time even with ResponseHeadersRead -- a fixed
    // 30s value here silently aborted every VOD stream partway through (reported live, 2026-09-21, "stops" at
    // inconsistent points typically under ~6 minutes). Real cancellation comes from the ASP.NET request's own token
    // (ffmpeg's connection via RequestAborted), and the live playlist has its own per-request timeout.
    private readonly HttpClient _upstream = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4) }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _tmdbHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<bool, HttpClient> _portalHttp = new();

    private readonly string _stampNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private string? _fingerprint;
    private PortalitoServices? _current;
    private string? _stampFingerprint;
    private long _stampGeneration;

    /// <param name="localBaseUrl">
    /// This server's own address as ffmpeg (running on it) reaches it, used when the proxy base URL is left empty.
    /// Jellyfin knows its port, HTTPS setting and base path; a hand-typed default doesn't.
    /// </param>
    public PortalitoRuntime(Func<PluginConfiguration> getConfig, Action saveConfig, TimeProvider clock, Func<string>? localBaseUrl = null)
    {
        _getConfig = getConfig;
        _saveConfig = saveConfig;
        _clock = clock;
        _localBaseUrl = localBaseUrl;
    }

    public PortalitoServices Get()
    {
        lock (_gate)
        {
            var config = _getConfig();
            if (string.IsNullOrEmpty(config.ProxySigningSecret))
            {
                config.ProxySigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                _saveConfig();
            }

            var fingerprint = Fingerprint(config);
            if (_current is null || fingerprint != _fingerprint)
            {
                _current = null;
                _current = Build(config);
                _fingerprint = fingerprint;
            }

            return _current;
        }
    }

    // Not a hash of the configuration: on a cache hit Jellyfin doesn't ask the channel at all and serves the items it last
    // stored under that folder, so a key that came back to an earlier value (config A -> B -> A within 3 hours) would
    // reuse A's old cache file and show B's listing (measured 2026-09-30). A change counter never repeats; the per-start
    // nonce keeps a restarted counter from colliding with the previous process's files.
    public string CacheStamp()
    {
        lock (_gate)
        {
            var fingerprint = Fingerprint(_getConfig());
            if (fingerprint != _stampFingerprint)
            {
                _stampFingerprint = fingerprint;
                _stampGeneration++;
            }

            return _stampNonce + _stampGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private PortalitoServices Build(PluginConfiguration config)
    {
        var options = PortalOptions.FromConfiguration(config);
        if (!_portalHttp.TryGetValue(config.SkipPortalTlsVerification, out var portalHttp))
        {
            portalHttp = HttpPortalTransport.CreateHttpClient(config.SkipPortalTlsVerification);
            _portalHttp[config.SkipPortalTlsVerification] = portalHttp;
        }

        var transport = new HttpPortalTransport(portalHttp, options);
        var portal = new PortalClient(options, transport, provisionedSn =>
        {
            // A fresh free-tier device was registered: persist its serial so later activations reuse it.
            config.DeviceSn = provisionedSn;
            _saveConfig();
        });
        var signer = new ProxyUrlSigner(config.ProxySigningSecret, ProxyBaseUrl(config), _clock);
        var contentAuth = BuildContentAuthSigner(config);
        var proxy = new PortalitoProxyService(_upstream, portal, signer, contentAuth, new ProxyIdentity(options.AppId, options.ApkVersion, options.CdnUserAgent), _clock);
        var catalogs = new CatalogBrowser(_clock, CatalogBrowser.ParseCatalogs(config.Catalogs));

        // Optional: without a key the TMDB fallback is simply off.
        var tmdb = string.IsNullOrWhiteSpace(config.TmdbApiKey)
            ? null
            : new Metadata.TmdbClient(new Metadata.HttpTmdbTransport(_tmdbHttp), config.TmdbApiKey.Trim(), _clock);
        var epgZone = Live.EpgMapper.ResolveZone(config.EpgTimeZone, out var epgZoneProblem);

        // TMDB-driven "Destacado" rows: only with a TMDB key and at least one valid configured row.
        var featuredRows = DiscoveryBrowser.ParseRows(config.FeaturedRows);
        var discovery = tmdb is not null && featuredRows.Count > 0 ? new DiscoveryBrowser(tmdb, _clock, featuredRows) : null;
        return new PortalitoServices(portal, signer, proxy, catalogs, tmdb, epgZone, epgZoneProblem, discovery);
    }

    /// <summary>Builds the CDN Content-Auth signer from config: signing method, salt bytes, and any non-standard MD5 deviation.</summary>
    private static IContentAuthSigner BuildContentAuthSigner(PluginConfiguration config)
    {
        var salt = string.IsNullOrWhiteSpace(config.ContentAuthSaltHex)
            ? Array.Empty<byte>()
            : ParseHex(config.ContentAuthSaltHex.Trim(), nameof(config.ContentAuthSaltHex));

        int[]? schedule = null;
        if (!string.IsNullOrWhiteSpace(config.ContentAuthMd5Schedule))
        {
            schedule = config.ContentAuthMd5Schedule
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var n) ? n : throw new PortalException($"Content-Auth MD5 schedule has a non-number: '{s}'."))
                .ToArray();
        }

        Dictionary<int, uint>? overrides = null;
        if (!string.IsNullOrWhiteSpace(config.ContentAuthMd5KOverrides))
        {
            overrides = new Dictionary<int, uint>();
            foreach (var pair in config.ContentAuthMd5KOverrides.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var index)
                    || !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    throw new PortalException($"Content-Auth MD5 override must be 'index:hex', got '{pair}'.");
                }

                overrides[index] = value;
            }
        }

        try
        {
            return new ConfigurableContentAuthSigner(config.ContentAuthMethod?.Trim() ?? string.Empty, salt, schedule, overrides);
        }
        catch (ArgumentException ex)
        {
            throw new PortalException($"Content-Auth signing config is invalid: {ex.Message}");
        }
    }

    private static byte[] ParseHex(string hex, string field)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new PortalException($"{field} must be hex, got '{hex}'.");
        }
    }

    /// <summary>The configured proxy base URL, or this server's own when it's left empty; either way a real http(s) URL.</summary>
    private string ProxyBaseUrl(PluginConfiguration config)
    {
        var configured = config.ProxyBaseUrl?.Trim();
        var url = string.IsNullOrEmpty(configured)
            ? _localBaseUrl?.Invoke() ?? throw new PortalException("The proxy base URL is empty and this server's own address isn't known; set it on the config page.")
            : configured;

        // Checked here: a typo (no scheme, a stray space) otherwise only showed up as ffmpeg failing every stream.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new PortalException($"The proxy base URL '{url}' isn't an http(s) address like http://127.0.0.1:8096.");
        }

        return url;
    }

    private static string Fingerprint(PluginConfiguration c)
        => string.Join(
            '\u001f',
            c.TripleDesKeyHex,
            c.Hosts,
            c.AppId,
            c.ApkVersion,
            c.DeviceSn,
            c.DeviceDrmId,
            c.DeviceToken,
            c.DeviceReserve1,
            c.SnTokenSalt,
            c.SkipPortalTlsVerification,
            c.ProxyBaseUrl,
            c.AccountEmail,
            c.AccountPassword,

            // Without it here, saving a key on the config page wouldn't take effect until a restart (the 2026-09-22 bug).
            c.TmdbApiKey,
            c.FeaturedRows,
            c.EpgTimeZone,
            c.ProxySigningSecret,
            c.ContentAuthMethod,
            c.ContentAuthSaltHex,
            c.ContentAuthMd5Schedule,
            c.ContentAuthMd5KOverrides,
            c.PortalCode,
            c.ApiBasePath,
            c.PortalApkVer,
            c.PortalSpkgVer,
            c.PortalUserAgent,
            c.CdnUserAgent,
            c.LoginPasswordSalt,
            c.LiveColumnCode,
            c.AllChannelsColumnId,
            c.Catalogs);
}
