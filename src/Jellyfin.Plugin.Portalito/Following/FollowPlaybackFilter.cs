using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Makes Jellyfin itself stream a "Siguiendo" episode, the way it streams the channel's. A .strm's URL points at this
/// server's proxy as ffmpeg reaches it (default <c>127.0.0.1</c>); left alone, Jellyfin offers it as a remote source a
/// client may direct-play, and jellyfin-web does exactly that with any remote http source
/// (<c>playbackmanager.js supportsDirectPlay</c>: "IsRemote" counts as reachable) -- so a browser would try to open
/// 127.0.0.1 on its own machine. Turning off direct play and direct stream for these items on the playback-info call
/// leaves transcoding, with stream copy still allowed: an HLS remux, as for channel items
/// (<see cref="Catalog.MediaSources.Vod"/>).
/// </summary>
public sealed class FollowPlaybackFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _library;
    private readonly IApplicationPaths _paths;

    public FollowPlaybackFilter(ILibraryManager library, IApplicationPaths paths)
    {
        _library = library;
        _paths = paths;
    }

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (IsPlaybackInfo(context.ActionDescriptor as ControllerActionDescriptor)
            && context.ActionArguments.TryGetValue("itemId", out var raw) && raw is Guid itemId
            && Plugin.Instance?.Configuration is { } config
            && _library.GetItemById(itemId) is { } item
            && FollowLibrary.Contains(FollowLibrary.Root(config, _paths.DataPath), item.Path))
        {
            // The query parameters win over the body's (MediaInfoController: "enableDirectPlay ??= dto.EnableDirectPlay").
            context.ActionArguments["enableDirectPlay"] = false;
            context.ActionArguments["enableDirectStream"] = false;
        }

        return next();
    }

    /// <summary><c>POST /Items/{itemId}/PlaybackInfo</c>, the call every current client makes before playing.</summary>
    internal static bool IsPlaybackInfo(ControllerActionDescriptor? action)
        => action is { ControllerName: "MediaInfo", ActionName: "GetPostedPlaybackInfo" };
}
