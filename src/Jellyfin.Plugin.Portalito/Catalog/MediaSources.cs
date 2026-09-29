using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>Builds the <see cref="MediaSourceInfo"/> Jellyfin hands to ffmpeg: always a signed URL on this plugin's own proxy.</summary>
internal static class MediaSources
{
    /// <summary>How long a proxy URL handed to Jellyfin stays valid; it must outlive a viewing session.</summary>
    public static readonly TimeSpan UrlValidity = TimeSpan.FromHours(12);

    /// <summary>
    /// How long a live playlist URL stays valid. ffmpeg re-requests the SAME playlist URL every few seconds for as long
    /// as the channel plays, so the URL's expiry is a hard cap on one continuous live session: at 12 hours a channel
    /// left on overnight started answering 403 and stopped. Segment URLs are re-signed on every playlist fetch, so
    /// only this one needs to be long-lived.
    /// </summary>
    public static readonly TimeSpan LiveUrlValidity = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a signed poster proxy URL stays valid. Unlike <see cref="UrlValidity"/> (regenerated on every
    /// playback request), an item's <c>ImageUrl</c> is set once when its folder is listed and then Jellyfin
    /// persists/reuses it on its own schedule -- a short expiry would start 403ing posters between browses. 30
    /// days comfortably outlives the daily <c>DataVersion</c> cache-bust cadence.
    /// </summary>
    public static readonly TimeSpan ImageUrlValidity = TimeSpan.FromDays(30);

    /// <summary>
    /// The media source id for a channel item: a GUID derived from the item id, the same on every call. It must be
    /// a GUID: Jellyfin 10.11.11's HLS master playlist does <c>Guid.Parse(MediaSourceId)</c> for trickplay whenever
    /// the source isn't "live", i.e. whenever it has a runtime (DynamicHlsHelper, IsSegmentedLiveStream =
    /// !RunTimeTicks.HasValue). Once probing started reporting a runtime, the item-id-shaped ids ("epi:...") made the
    /// web player's playback 500 with a FormatException (measured live 2026-09-24).
    /// </summary>
    public static string SourceIdFor(string itemId)
        => new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(itemId))).ToString("N");

    public static MediaSourceInfo Live(string channelCode, string playlistUrl) => new()
    {
        Id = channelCode,
        Path = playlistUrl,
        Protocol = MediaProtocol.Http,
        IsRemote = true,
        IsInfiniteStream = true,
        RequiresOpening = false,
        RequiresClosing = false,
        ReadAtNativeFramerate = false,
        SupportsProbing = true,

        // The proxy URL points at this Jellyfin (default 127.0.0.1), which a client cannot be assumed to reach, so
        // Jellyfin's own ffmpeg must always be the one to open it.
        SupportsDirectPlay = false,
        SupportsDirectStream = false,
        SupportsTranscoding = true,
        MediaStreams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = -1 },
            new() { Type = MediaStreamType.Audio, Index = -1 },
        },
    };

    /// <summary>
    /// <paramref name="container"/> must be the CDN file's real container ("ts" or "mp4", from the portal's
    /// videoFormat) -- ffmpeg is told to demux as exactly this (a forced <c>-f</c>), so a wrong guess fails
    /// outright ("moov atom not found", measured live) rather than falling back to auto-probing.
    /// </summary>
    public static MediaSourceInfo Vod(string id, string url, string container) => new()
    {
        Id = SourceIdFor(id),
        Path = url,
        Protocol = MediaProtocol.Http,
        Container = container,
        IsRemote = true,
        SupportsProbing = true,
        SupportsDirectPlay = false,
        SupportsDirectStream = false,
        SupportsTranscoding = true,
        MediaStreams = new List<MediaStream>(),
    };
}
