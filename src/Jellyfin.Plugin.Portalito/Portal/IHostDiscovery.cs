namespace Jellyfin.Plugin.Portalito.Portal;

/// <summary>
/// Resolves the live portal hosts from the configured seeds (SLB host discovery). Returns an empty list when discovery
/// is off or every seed fails, so the caller falls back to the seeds. Exists so the client is testable without a network.
/// </summary>
public interface IHostDiscovery
{
    Task<IReadOnlyList<string>> ResolvePortalHostsAsync(CancellationToken cancellationToken);
}
