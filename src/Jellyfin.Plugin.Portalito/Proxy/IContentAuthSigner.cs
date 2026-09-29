namespace Jellyfin.Plugin.Portalito.Proxy;

/// <summary>
/// Signs the CDN <c>Content-Auth</c> header for a live playlist or segment request. How the per-request signature is
/// computed is portal-specific, so the concrete signer is built from the operator's config (see
/// <see cref="ConfigurableContentAuthSigner"/>). The public build ships only the configurable implementation; no
/// portal's signing parameters are baked in.
/// </summary>
public interface IContentAuthSigner
{
    /// <summary>
    /// Builds the fresh <c>Content-Auth</c> value: the base auth query string from the load-balancer response plus a
    /// freshly signed <c>start_moment</c>.
    /// </summary>
    /// <param name="baseAuth">The base auth query string for this CDN entry.</param>
    /// <param name="token">The per-session token carried inside <paramref name="baseAuth"/>.</param>
    /// <param name="startMomentMs">The signing moment, in unix milliseconds.</param>
    string BuildContentAuth(string baseAuth, string token, long startMomentMs);
}
