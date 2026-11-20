using Jellyfin.Plugin.Portalito.Configuration;

namespace Jellyfin.Plugin.Portalito.Portal;

/// <summary>Immutable snapshot of the portal settings taken from the plugin configuration.</summary>
public sealed record PortalOptions
{
    public string TripleDesKeyHex { get; init; } = string.Empty;

    public IReadOnlyList<string> Hosts { get; init; } = Array.Empty<string>();

    public string AppId { get; init; } = string.Empty;

    public string ApkVersion { get; init; } = string.Empty;

    public string DeviceSn { get; init; } = string.Empty;

    public string DeviceDrmId { get; init; } = string.Empty;

    public string DeviceToken { get; init; } = string.Empty;

    public string DeviceReserve1 { get; init; } = string.Empty;

    public string SnTokenSalt { get; init; } = string.Empty;

    public string Portal { get; init; } = string.Empty;

    public string ApiBasePath { get; init; } = string.Empty;

    public string ApkVer { get; init; } = string.Empty;

    public string SpkgVer { get; init; } = string.Empty;

    /// <summary>Secret for the per-request signature headers (empty = not sent).</summary>
    public string RequestSignSecret { get; init; } = string.Empty;

    /// <summary>AES key (ASCII) for SLB host discovery (empty = discovery off).</summary>
    public string DiscoveryAesKey { get; init; } = string.Empty;

    /// <summary>SLB discovery request path (empty = discovery off).</summary>
    public string DiscoveryPath { get; init; } = string.Empty;

    /// <summary>Discovered service whose hosts become the portal hosts; defaults to <c>portal</c>.</summary>
    public string DiscoveryService { get; init; } = "portal";

    /// <summary>Whether to record the live discovery-seed hosts so bootstrap survives the configured seeds being retired.</summary>
    public bool PersistDiscoveredSeeds { get; init; }

    /// <summary>Discovered service whose hosts are the discovery bootstrap seeds (answer the discovery request); defaults to <c>dcs</c>.</summary>
    public string DiscoverySeedService { get; init; } = "dcs";

    /// <summary>Bootstrap seeds the plugin has learned (host:port), tried after <see cref="Hosts"/> when resolving discovery.</summary>
    public IReadOnlyList<string> DiscoverySeeds { get; init; } = Array.Empty<string>();

    /// <summary>Gets a value indicating whether SLB host discovery is configured.</summary>
    public bool DiscoveryEnabled => DiscoveryAesKey.Length > 0 && DiscoveryPath.Length > 0;

    public string PortalUserAgent { get; init; } = string.Empty;

    public string CdnUserAgent { get; init; } = string.Empty;

    public string LoginPasswordSalt { get; init; } = string.Empty;

    public string LiveColumnCode { get; init; } = string.Empty;

    public long AllChannelsColumnId { get; init; }

    public string AccountEmail { get; init; } = string.Empty;

    public string AccountPassword { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether to log in with an account instead of activating the device anonymously.</summary>
    public bool HasAccount => AccountEmail.Length > 0 && AccountPassword.Length > 0;

    public static PortalOptions FromConfiguration(PluginConfiguration config) => new()
    {
        TripleDesKeyHex = config.TripleDesKeyHex.Trim(),
        Hosts = ParseHosts(config.Hosts),
        AppId = config.AppId.Trim(),
        ApkVersion = config.ApkVersion.Trim(),
        DeviceSn = config.DeviceSn.Trim(),
        DeviceDrmId = config.DeviceDrmId.Trim(),
        DeviceToken = config.DeviceToken.Trim(),
        DeviceReserve1 = config.DeviceReserve1.Trim(),
        SnTokenSalt = config.SnTokenSalt.Trim(),
        Portal = config.PortalCode.Trim(),
        ApiBasePath = config.ApiBasePath.Trim(),
        ApkVer = config.PortalApkVer.Trim(),
        SpkgVer = config.PortalSpkgVer.Trim(),
        RequestSignSecret = config.RequestSignSecret.Trim(),
        DiscoveryAesKey = config.DiscoveryAesKey.Trim(),
        DiscoveryPath = config.DiscoveryPath.Trim(),
        DiscoveryService = string.IsNullOrWhiteSpace(config.DiscoveryService) ? "portal" : config.DiscoveryService.Trim(),
        PersistDiscoveredSeeds = config.PersistDiscoveredSeeds,
        DiscoverySeedService = string.IsNullOrWhiteSpace(config.DiscoverySeedService) ? "dcs" : config.DiscoverySeedService.Trim(),
        DiscoverySeeds = ParseHosts(config.DiscoveredSeeds),
        PortalUserAgent = config.PortalUserAgent.Trim(),
        CdnUserAgent = config.CdnUserAgent.Trim(),
        LoginPasswordSalt = config.LoginPasswordSalt,
        LiveColumnCode = config.LiveColumnCode.Trim(),
        AllChannelsColumnId = long.TryParse(config.AllChannelsColumnId.Trim(), out var col) ? col : 0,
        AccountEmail = config.AccountEmail.Trim(),

        // Not trimmed: a password can legitimately start or end with a space.
        AccountPassword = config.AccountPassword,
    };

    public static IReadOnlyList<string> ParseHosts(string? commaSeparated)
        => (commaSeparated ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(StripSchemeAndPath)
            .Where(h => h.Length > 0)
            .ToArray();

    /// <summary>Throws <see cref="PortalException"/> when a required setting is missing.</summary>
    public void Validate()
    {
        if (string.IsNullOrEmpty(TripleDesKeyHex))
        {
            throw new PortalException("The 3DES key is not configured.");
        }

        // Checked here, not only in PortalCipher (which throws ArgumentException): callers treat PortalException as
        // "not configured yet", so a mistyped key hides the channel instead of breaking Jellyfin's channel listing.
        if (TripleDesKeyHex.Length is not (32 or 48) || !TripleDesKeyHex.All(Uri.IsHexDigit))
        {
            throw new PortalException("The 3DES key must be 32 or 48 hex characters.");
        }

        if (Hosts.Count == 0)
        {
            throw new PortalException("No portal hosts are configured.");
        }

        if (string.IsNullOrEmpty(AppId))
        {
            throw new PortalException("The app id is not configured.");
        }

        if ((AccountEmail.Length > 0) != (AccountPassword.Length > 0))
        {
            throw new PortalException("The Portalito account needs both an email and a password (or neither, for anonymous activation).");
        }
    }

    /// <summary>Reduces a host value to a bare host:port, dropping any scheme and path (shared with discovered hosts).</summary>
    internal static string StripSchemeAndPath(string host)
    {
        var h = host;
        var scheme = h.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            h = h[(scheme + 3)..];
        }

        var slash = h.IndexOf('/');
        return slash >= 0 ? h[..slash] : h;
    }
}
