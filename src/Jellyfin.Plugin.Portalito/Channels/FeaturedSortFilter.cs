using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>
/// Lists a Destacado row best-rated first. A channel can't order its own items: Jellyfin sorts every channel folder
/// with the sort the client asks for (v10.11 <c>ChannelManager.GetChannelItemsInternal</c> ends in
/// <c>LibraryManager.GetItemsResult(query)</c>), and clients ask for A-Z by default (jellyfin-web: <c>IsFolder,SortName</c>).
/// So on the item-listing calls for a row folder, a plain A-Z request is turned into community rating (highest first,
/// unrated last), then name. Any other sort the client picks -- release date, Z-A, rating ascending... -- is kept.
/// Ratings come from the portal or TMDB (see <see cref="PortalitoVodChannel"/>); the <c>SortFeaturedByRating</c> setting
/// turns this off.
/// </summary>
public sealed class FeaturedSortFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _library;

    public FeaturedSortFilter(ILibraryManager library)
    {
        _library = library;
    }

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (Plugin.Instance?.Configuration is { SortFeaturedByRating: true }
            && FolderArgument(context.ActionDescriptor as ControllerActionDescriptor) is { } argument
            && context.ActionArguments.TryGetValue(argument, out var raw) && raw is Guid folderId && !folderId.Equals(Guid.Empty)
            && IsAlphabetical(Argument<ItemSortBy>(context, "sortBy"), Argument<SortOrder>(context, "sortOrder"))
            && IsFeaturedRow(_library.GetItemById(folderId), ChannelId(_library)))
        {
            // Both arrays in full: Jellyfin pairs them by position (RequestHelpers.GetOrderBy).
            context.ActionArguments["sortBy"] = new[] { ItemSortBy.CommunityRating, ItemSortBy.SortName };
            context.ActionArguments["sortOrder"] = new[] { SortOrder.Descending, SortOrder.Ascending };
        }

        return next();
    }

    /// <summary>
    /// The argument naming the folder being listed: <c>parentId</c> on <c>/Items</c> (what jellyfin-web and most apps
    /// call) and its legacy <c>/Users/{userId}/Items</c> route, <c>folderId</c> on <c>/Channels/{channelId}/Items</c>.
    /// </summary>
    internal static string? FolderArgument(ControllerActionDescriptor? action) => action switch
    {
        { ControllerName: "Items", ActionName: "GetItems" or "GetItemsByUserIdLegacy" } => "parentId",
        { ControllerName: "Channels", ActionName: "GetChannelItems" } => "folderId",
        _ => null,
    };

    /// <summary>
    /// Whether the requested sort is plain A-Z: nothing, or only name/folder keys, all ascending. That's every client's
    /// default -- and also what a person who picked "Name" gets, which can't be told apart; Z-A still sorts by name.
    /// </summary>
    internal static bool IsAlphabetical(IReadOnlyList<ItemSortBy> sortBy, IReadOnlyList<SortOrder> sortOrder)
        => sortBy.All(s => s is ItemSortBy.SortName or ItemSortBy.Name or ItemSortBy.IsFolder)
           && sortOrder.All(o => o == SortOrder.Ascending);

    /// <summary>Whether <paramref name="folder"/> is one of this channel's Destacado rows (TMDB or portal rows alike).</summary>
    internal static bool IsFeaturedRow(BaseItem? folder, Guid channelId)
        => folder is Folder
           && folder.ChannelId.Equals(channelId)
           && VodItemId.TryParse(folder.ExternalId, out var id)
           && id.Kind == VodItemKind.Row;

    /// <summary>This channel's Jellyfin id, derived from its name the way Jellyfin does (<c>ChannelManager.GetInternalChannelId</c>).</summary>
    internal static Guid ChannelId(ILibraryManager library)
        => library.GetNewItemId("Channel " + PortalitoVodChannel.ChannelName, typeof(Channel));

    private static IReadOnlyList<T> Argument<T>(ActionExecutingContext context, string name)
        => context.ActionArguments.TryGetValue(name, out var value) && value is T[] array ? array : Array.Empty<T>();
}
