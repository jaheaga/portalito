using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Makes Jellyfin itself stream a "Siguiendo" episode, the way it streams the channel's. A .strm's URL points at this
/// server's proxy as ffmpeg reaches it (default <c>127.0.0.1</c>); left alone, Jellyfin offers it as a remote source a
/// client may direct-play, and jellyfin-web does exactly that with any remote http source
/// (<c>playbackmanager.js supportsDirectPlay</c>: "IsRemote" counts as reachable) -- so a browser would try to open
/// 127.0.0.1 on its own machine. On the playback-info calls for these items:
/// <list type="bullet">
/// <item>before the action, direct play and direct stream are turned off, so Jellyfin builds a transcoding URL (an HLS
/// remux, as for channel items, <see cref="Catalog.MediaSources.Vod"/>) -- this only takes effect when the client sends a
/// device profile;</item>
/// <item>after it, every returned source of the item is marked not direct-playable either way (the GET call, or a POST
/// without a profile, skip Jellyfin's profile pass entirely);</item>
/// <item>before it, too, the episode's subtitles are fetched (<see cref="FollowSubtitles"/>) -- only for a user who can
/// see the item, since that costs a portal play call.</item>
/// </list>
/// </summary>
public sealed class FollowPlaybackFilter : IAsyncActionFilter
{
    /// <summary>The claim Jellyfin's auth handler puts the caller's user id in (<c>Jellyfin.Api InternalClaimTypes.UserId</c>).</summary>
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IApplicationPaths _paths;
    private readonly FollowSubtitles _subtitles;

    public FollowPlaybackFilter(ILibraryManager library, IUserManager users, IApplicationPaths paths, FollowSubtitles subtitles)
    {
        _library = library;
        _users = users;
        _paths = paths;
        _subtitles = subtitles;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var item = IsPlaybackInfo(context.ActionDescriptor as ControllerActionDescriptor)
                   && context.ActionArguments.TryGetValue("itemId", out var raw) && raw is Guid itemId
                   && Plugin.Instance?.Configuration is { } config
                   && _library.GetItemById(itemId) is { } found
                   && FollowLibrary.Contains(FollowLibrary.Root(config, _paths.DataPath), found.Path)
            ? found
            : null;

        if (item is not null)
        {
            // The query parameters win over the body's (MediaInfoController: "enableDirectPlay ??= dto.EnableDirectPlay").
            context.ActionArguments["enableDirectPlay"] = false;
            context.ActionArguments["enableDirectStream"] = false;

            // Before the action: its re-probe of the .strm is what picks up the subtitle files written here.
            if (Caller(context) is { } user && item.IsVisible(user, false))
            {
                await _subtitles.EnsureAsync(item.Path, context.HttpContext.RequestAborted).ConfigureAwait(false);
            }
        }

        var executed = await next().ConfigureAwait(false);
        if (item is not null && executed.Result is ObjectResult { Value: PlaybackInfoResponse response })
        {
            ForceServerStreaming(response);
        }
    }

    /// <summary>Marks every source not direct-playable or direct-streamable; Jellyfin's ffmpeg opens the URL instead.</summary>
    internal static void ForceServerStreaming(PlaybackInfoResponse response)
    {
        foreach (var source in response.MediaSources)
        {
            source.SupportsDirectPlay = false;
            source.SupportsDirectStream = false;
        }
    }

    /// <summary>The <c>/Items/{itemId}/PlaybackInfo</c> calls (POST, which current clients make, and the older GET).</summary>
    internal static bool IsPlaybackInfo(ControllerActionDescriptor? action)
        => action is { ControllerName: "MediaInfo", ActionName: "GetPostedPlaybackInfo" or "GetPlaybackInfo" };

    /// <summary>The calling user: from the authenticated request, else the action's <c>userId</c> argument.</summary>
    private Jellyfin.Database.Implementations.Entities.User? Caller(ActionExecutingContext context)
    {
        var claim = context.HttpContext.User.FindFirst(UserIdClaim)?.Value;
        if (Guid.TryParse(claim, out var fromClaim) && !fromClaim.Equals(Guid.Empty))
        {
            return _users.GetUserById(fromClaim);
        }

        return context.ActionArguments.TryGetValue("userId", out var raw) && raw is Guid fromArgument && !fromArgument.Equals(Guid.Empty)
            ? _users.GetUserById(fromArgument)
            : null;
    }
}
