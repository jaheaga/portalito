namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>One signal that someone follows a show: when they last played one of its episodes, or that it's a favorite.</summary>
public sealed record ShowActivity(string ShowId, DateTime? LastPlayedUtc, bool Favorite = false);

/// <summary>Which shows the "Siguiendo" library mirrors. Separate from Jellyfin so the rules can be tested.</summary>
public static class FollowPlanner
{
    /// <summary>A show nobody has played for this long (and nobody marked favorite) leaves the library.</summary>
    public static readonly TimeSpan ActivityWindow = TimeSpan.FromDays(60);

    /// <summary>Safety cap on mirrored shows. Owner's estimate from production history: 10-20 at any time.</summary>
    public const int MaxShows = 100;

    /// <summary>
    /// The shows to mirror, most recently played first: every show someone marked favorite or played within
    /// <see cref="ActivityWindow"/>, capped at <see cref="MaxShows"/> (favorites go first when the cap bites).
    /// </summary>
    public static IReadOnlyList<string> Plan(IEnumerable<ShowActivity> activity, DateTime nowUtc)
    {
        var cutoff = nowUtc - ActivityWindow;
        return activity
            .GroupBy(a => a.ShowId, StringComparer.Ordinal)
            .Select(g => (ShowId: g.Key, Last: g.Max(a => a.LastPlayedUtc), Favorite: g.Any(a => a.Favorite)))
            .Where(s => s.Favorite || s.Last >= cutoff)
            .OrderByDescending(s => s.Favorite)
            .ThenByDescending(s => s.Last ?? DateTime.MinValue)
            .ThenBy(s => s.ShowId, StringComparer.Ordinal)
            .Take(MaxShows)
            .Select(s => s.ShowId)
            .ToList();
    }
}
