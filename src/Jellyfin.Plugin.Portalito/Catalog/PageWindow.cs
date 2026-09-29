namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>
/// Maps Jellyfin's <c>StartIndex</c>/<c>Limit</c> window onto the portal's fixed-size 1-based pages. The portal only
/// pages by <c>pageNum</c>, so a window that straddles a page boundary needs several pages and a slice.
/// </summary>
public readonly record struct PageWindow(int Start, int Limit, int PageSize)
{
    /// <summary>Gets the first portal page (1-based) the window touches.</summary>
    public int FirstPage => (Start / PageSize) + 1;

    /// <summary>Gets the last portal page (1-based) the window touches.</summary>
    public int LastPage => ((Start + Limit - 1) / PageSize) + 1;

    /// <summary>Gets how many items to drop from the front of the concatenated pages.</summary>
    public int Skip => Start % PageSize;

    public static PageWindow Create(int? start, int? limit, int pageSize)
    {
        var s = Math.Max(0, start ?? 0);
        var l = limit is > 0 ? limit.Value : pageSize;
        return new PageWindow(s, l, pageSize);
    }
}
