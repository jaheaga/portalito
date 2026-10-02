using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public sealed class FeaturedSortFilterTests
{
    private static readonly Guid Channel = Guid.NewGuid();

    [Fact]
    public void Only_the_item_listing_calls_are_rewritten_each_by_its_folder_argument()
    {
        Assert.Equal("parentId", FeaturedSortFilter.FolderArgument(new ControllerActionDescriptor { ControllerName = "Items", ActionName = "GetItems" }));
        Assert.Equal("parentId", FeaturedSortFilter.FolderArgument(new ControllerActionDescriptor { ControllerName = "Items", ActionName = "GetItemsByUserIdLegacy" }));
        Assert.Equal("folderId", FeaturedSortFilter.FolderArgument(new ControllerActionDescriptor { ControllerName = "Channels", ActionName = "GetChannelItems" }));
        Assert.Null(FeaturedSortFilter.FolderArgument(new ControllerActionDescriptor { ControllerName = "Items", ActionName = "GetResumeItems" }));
        Assert.Null(FeaturedSortFilter.FolderArgument(null));
    }

    [Fact]
    public void A_plain_a_to_z_request_counts_as_the_default_and_any_other_sort_is_kept()
    {
        Assert.True(FeaturedSortFilter.IsAlphabetical(Array.Empty<ItemSortBy>(), Array.Empty<SortOrder>()));
        Assert.True(FeaturedSortFilter.IsAlphabetical(new[] { ItemSortBy.IsFolder, ItemSortBy.SortName }, new[] { SortOrder.Ascending })); // jellyfin-web
        Assert.True(FeaturedSortFilter.IsAlphabetical(new[] { ItemSortBy.SortName }, Array.Empty<SortOrder>()));

        Assert.False(FeaturedSortFilter.IsAlphabetical(new[] { ItemSortBy.SortName }, new[] { SortOrder.Descending })); // Z-A
        Assert.False(FeaturedSortFilter.IsAlphabetical(new[] { ItemSortBy.CommunityRating, ItemSortBy.SortName }, new[] { SortOrder.Ascending }));
        Assert.False(FeaturedSortFilter.IsAlphabetical(new[] { ItemSortBy.ProductionYear, ItemSortBy.PremiereDate, ItemSortBy.SortName }, new[] { SortOrder.Descending }));
    }

    [Theory]
    [InlineData("row:123456789:tmdb", true)] // a TMDB row
    [InlineData("row:0:top", true)] // a portal row
    [InlineData("fea:root", false)] // Destacado itself: the rows keep A-Z
    [InlineData("flt:0:all", false)] // Descubrir
    [InlineData(null, false)]
    public void Only_this_channels_destacado_rows_are_sorted_by_rating(string? externalId, bool expected)
    {
        Assert.Equal(expected, FeaturedSortFilter.IsFeaturedRow(new Folder { ChannelId = Channel, ExternalId = externalId }, Channel));
    }

    [Fact]
    public void Another_channels_folder_or_a_title_is_not_a_row()
    {
        var row = VodItemId.Row(0, "top").ToString();

        Assert.False(FeaturedSortFilter.IsFeaturedRow(new Folder { ChannelId = Guid.NewGuid(), ExternalId = row }, Channel));
        Assert.False(FeaturedSortFilter.IsFeaturedRow(new Movie { ChannelId = Channel, ExternalId = row }, Channel));
        Assert.False(FeaturedSortFilter.IsFeaturedRow(null, Channel));
    }
}
