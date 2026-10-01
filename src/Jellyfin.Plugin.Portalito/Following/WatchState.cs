using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Carries a user's progress on an episode from its channel copy to its "Siguiendo" copy, so Next Up knows where
/// they are in the show: what they watched in the channel before the show was mirrored, and anything watched in the
/// channel since. The newer play wins; the library copy is never overwritten with an older state.
/// </summary>
public static class WatchState
{
    /// <summary>Whether <paramref name="channel"/> holds a play newer than anything <paramref name="library"/> knows.</summary>
    public static bool ShouldCopy(UserItemData channel, UserItemData library)
        => (channel.Played || channel.PlaybackPositionTicks > 0)
           && channel.LastPlayedDate is { } played
           && (library.LastPlayedDate is null || played > library.LastPlayedDate);

    /// <summary>Copies the channel copy's progress onto the library copy.</summary>
    public static void Copy(UserItemData channel, UserItemData library)
    {
        library.Played = channel.Played;
        library.PlaybackPositionTicks = channel.PlaybackPositionTicks;
        library.PlayCount = Math.Max(library.PlayCount, Math.Max(channel.PlayCount, 1));
        library.LastPlayedDate = channel.LastPlayedDate;
    }
}
