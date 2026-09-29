namespace Jellyfin.Plugin.Portalito.Portal;

/// <summary>Posts an already-encrypted body to one portal host. Exists so the client is testable without a network.</summary>
public interface IPortalTransport
{
    /// <summary>POSTs <paramref name="wireBody"/> to <c>https://{host}/api/core/{path}</c> and returns the response text.</summary>
    Task<string> PostAsync(string host, string path, string wireBody, CancellationToken cancellationToken);
}
