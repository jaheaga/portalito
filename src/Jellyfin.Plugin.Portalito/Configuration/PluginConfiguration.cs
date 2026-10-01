using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Portalito.Configuration;

/// <summary>
/// Plugin settings, edited on the plugin's config page. Every portal secret lives here (Jellyfin's plugin
/// config XML), never in the assembly.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the 3DES portal key (hex, 32 or 48 chars).</summary>
    public string TripleDesKeyHex { get; set; } = string.Empty;

    /// <summary>Gets or sets the comma-separated portal API hosts (primary first, then fallbacks).</summary>
    public string Hosts { get; set; } = string.Empty;

    /// <summary>Gets or sets the APK application id sent as <c>appId</c> and the <c>App</c> header.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Gets or sets the APK version sent as <c>apkVersion</c>/<c>appVer</c> and the <c>App-Version</c> header.</summary>
    public string ApkVersion { get; set; } = string.Empty;

    // --- Portal protocol identity (advanced) ---
    // Everything a portal's middleware expects to identify the client. All blank by default: the plugin is a blank
    // framework, and the operator fills in their own portal's values. None of this is baked into the build.

    /// <summary>Gets or sets the portal code sent as <c>portalCode</c>.</summary>
    public string PortalCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the API base path on each host (e.g. <c>/api/core/</c>); the portal path is appended.</summary>
    public string ApiBasePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the value sent as the <c>apkVer</c> header (distinct from <see cref="ApkVersion"/>).</summary>
    public string PortalApkVer { get; set; } = string.Empty;

    /// <summary>Gets or sets the value sent as the <c>spkgVer</c> header and the <c>sysVersion</c> body field.</summary>
    public string PortalSpkgVer { get; set; } = string.Empty;

    /// <summary>Gets or sets the User-Agent sent to the portal API.</summary>
    public string PortalUserAgent { get; set; } = string.Empty;

    /// <summary>Gets or sets the User-Agent sent to the streaming CDN.</summary>
    public string CdnUserAgent { get; set; } = string.Empty;

    /// <summary>Gets or sets the salt appended to the password before hashing at login.</summary>
    public string LoginPasswordSalt { get; set; } = string.Empty;

    /// <summary>Gets or sets the live column code (the recommend-column code that lists the live categories).</summary>
    public string LiveColumnCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the column id whose live listing is every channel; empty/0 if the portal has none.</summary>
    public string AllChannelsColumnId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the VOD catalogs to browse, as <c>code:Label</c> pairs separated by commas or newlines
    /// (e.g. <c>movies_root:Movies, series_root:Series</c>). Empty means no VOD catalogs are shown.
    /// </summary>
    public string Catalogs { get; set; } = string.Empty;

    /// <summary>Gets or sets the device serial used by free-tier activation.</summary>
    public string DeviceSn { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional device DRM id (the portal accepts empty).</summary>
    public string DeviceDrmId { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional device token (the portal accepts empty).</summary>
    public string DeviceToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional device reserve1 field (the portal accepts empty).</summary>
    public string DeviceReserve1 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the salt that derives a new device serial from the portal's snToken
    /// (<c>sn = MD5(snToken + salt)</c>). When the device serial is left blank the plugin provisions a fresh
    /// free-tier device (snToken handshake) and saves the serial it gets; this salt is only needed if the portal
    /// does not return the serial directly. Blank by default.
    /// </summary>
    public string SnTokenSalt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether portal TLS certificates are not validated. Off by default: the login
    /// request carries the account's password hash under a 3DES key anyone can pull from the APK, so without
    /// certificate checks anyone on the network path can capture a replayable credential. The reference client runs
    /// with verify off; turn this on only if the portal hosts really do present invalid certificates. Existing
    /// installs keep whatever they saved (Jellyfin persists every setting).
    /// </summary>
    public bool SkipPortalTlsVerification { get; set; }

    /// <summary>
    /// Gets or sets the base URL ffmpeg uses to reach this Jellyfin's proxy endpoints (e.g. http://127.0.0.1:8096).
    /// Empty (the default) means this server's own local address as Jellyfin reports it -- right port, HTTPS and base
    /// path included. Installs that saved the old hardcoded default keep it.
    /// </summary>
    public string ProxyBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time zone the portal's guide times are in (an IANA id such as <c>America/Bogota</c>). The
    /// portal sends bare local times with no zone; empty means the server's own zone.
    /// </summary>
    public string EpgTimeZone { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional Portalito account email. With an account (email and password both set) the plugin logs
    /// in instead of activating the device anonymously -- live TV needs an account; catalog and VOD don't. An
    /// account holds one session at a time: logging in here signs out whatever device used it before.
    /// </summary>
    public string AccountEmail { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Portalito account password (see <see cref="AccountEmail"/>).</summary>
    public string AccountPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional TMDB v3 API key. When set, TMDB fills in synopses and posters Portalito leaves blank, and
    /// the <see cref="FeaturedRows"/> (if any) are built from TMDB lists -- limited to titles the portal has.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The out-of-the-box "Destacado" rows: generic TMDB lists (no portal-specific value), so a fresh install with a TMDB
    /// key gets them without any setup. Owner decision 2026-09-30.
    /// </summary>
    public const string DefaultFeaturedRows =
        "Tendencias | trending | window=week; Estrenos en cine | now-playing; Próximamente | upcoming; Películas populares | popular-movies; Series populares | popular-tv; En emisión hoy | airing-today; Anime del momento | discover-tv | country=JP,KR genre=16 pages=3; Películas de anime | discover-movies | country=JP,KR genre=16; Series coreanas | discover-tv | country=KR nogenre=16,10764,10767 minvotes=20 pages=3; Películas mejor valoradas | top-movies; Series mejor valoradas | top-tv";

    /// <summary>
    /// Gets or sets the "Destacado" rows, built from TMDB lists (needs <see cref="TmdbApiKey"/>): rows separated by
    /// <c>;</c> or newlines, each <c>Label | source | key=value ...</c>. Only titles the portal has are listed.
    /// Defaults to <see cref="DefaultFeaturedRows"/> (also for saved configs that predate the setting); an empty value
    /// switches back to the portal's own rows (newest and best-rated per catalog).
    /// </summary>
    public string FeaturedRows { get; set; } = DefaultFeaturedRows;

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="FeaturedRows"/> has been given its default once. Not on the
    /// config page. Lets a config saved by 0.1.0.4 -- when the default was empty -- pick up the default rows once, while
    /// a field cleared on purpose afterwards stays cleared.
    /// </summary>
    public bool FeaturedRowsSeeded { get; set; }

    /// <summary>
    /// One-time upgrades of a saved configuration; returns whether anything changed (so the caller saves it). Today: an
    /// empty <see cref="FeaturedRows"/> that was never seeded gets <see cref="DefaultFeaturedRows"/>.
    /// </summary>
    public bool ApplyMigrations()
    {
        if (FeaturedRowsSeeded)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(FeaturedRows))
        {
            FeaturedRows = DefaultFeaturedRows;
        }

        FeaturedRowsSeeded = true;
        return true;
    }

    /// <summary>
    /// Gets or sets an optional image URL for the channel (http/https). Empty (the default) uses the built-in Portalito
    /// logo. Jellyfin caches channel images: after changing it, refresh the channel's images.
    /// </summary>
    public string ChannelImageUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret that signs proxy URLs. Generated on first use when empty.</summary>
    public string ProxySigningSecret { get; set; } = string.Empty;

    // --- Content-Auth signing (advanced) ---
    // The CDN's per-request Content-Auth signature. Portal-specific; nothing is baked into the build. Empty method +
    // salt with standard MD5 (the defaults) produce an inert signature no real CDN accepts. See
    // Proxy/ConfigurableContentAuthSigner.

    /// <summary>Gets or sets the <c>sign2_method</c> value used in the Content-Auth signature.</summary>
    public string ContentAuthMethod { get; set; } = string.Empty;

    /// <summary>Gets or sets the Content-Auth signing salt, as hex bytes appended before hashing.</summary>
    public string ContentAuthSaltHex { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional non-standard MD5 round-1 schedule (16 comma-separated indices 0-15); empty = standard MD5.</summary>
    public string ContentAuthMd5Schedule { get; set; } = string.Empty;

    /// <summary>Gets or sets optional MD5 K-constant overrides (<c>index:hex</c> pairs, comma-separated); empty = standard MD5.</summary>
    public string ContentAuthMd5KOverrides { get; set; } = string.Empty;
}
