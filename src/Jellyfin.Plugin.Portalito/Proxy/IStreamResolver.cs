using Jellyfin.Plugin.Portalito.Portal;

namespace Jellyfin.Plugin.Portalito.Proxy;

/// <summary>Turns a channel / content id into CDN session data. Implemented by <see cref="PortalClient"/>.</summary>
public interface IStreamResolver
{
    Task<LiveStream> ResolveLiveAsync(string channelCode, CancellationToken cancellationToken);

    Task<VodStream> ResolveVodAsync(string contentId, string seriesContentId, CancellationToken cancellationToken);
}
