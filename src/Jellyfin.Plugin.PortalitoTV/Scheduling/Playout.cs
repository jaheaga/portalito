namespace Jellyfin.Plugin.PortalitoTV.Scheduling;

/// <summary>A channel's persisted schedule and the state of its collections.</summary>
public sealed class Playout
{
    /// <summary>Gets or sets the channel number.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Gets or sets how far the schedule is built (end of the last item).</summary>
    public DateTime BuiltUntilUtc { get; set; }

    /// <summary>Gets or sets the scheduled items.</summary>
    public List<PlayoutItem> Items { get; set; } = new();

    /// <summary>Gets or sets where each collection is inside this channel.</summary>
    public Dictionary<string, CollectionState> Collections { get; set; } = new();

    /// <summary>The state of a collection, created if it is new.</summary>
    /// <param name="key">Collection key.</param>
    /// <returns>The state.</returns>
    public CollectionState State(string key)
    {
        if (!Collections.TryGetValue(key, out var s))
        {
            s = new CollectionState();
            Collections[key] = s;
        }

        return s;
    }
}

/// <summary>Where a collection is inside a channel.</summary>
public sealed class CollectionState
{
    /// <summary>Gets or sets what is left to air of the current round (random mode), in order.</summary>
    public List<Guid> Bag { get; set; } = new();

    /// <summary>Gets or sets the last item aired (so a new round doesn't start with it again).</summary>
    public Guid LastPlayed { get; set; }

    /// <summary>Gets or sets the season of the next episode to air (chronological mode).</summary>
    public int NextSeason { get; set; }

    /// <summary>Gets or sets the number of the next episode to air (chronological mode).</summary>
    public int NextEpisode { get; set; }
}

/// <summary>An item available to schedule.</summary>
/// <param name="ItemId">The Jellyfin episode or movie.</param>
/// <param name="Duration">Its length.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="Template">Guide data; Start/End are filled in when it is scheduled.</param>
public sealed record ContentEntry(Guid ItemId, TimeSpan Duration, int Season, int Episode, PlayoutItem Template);

/// <summary>Content collections: "series:&lt;id&gt;", "movies", or "mix:&lt;key1&gt;|&lt;key2&gt;".</summary>
public interface IContentLookup
{
    /// <summary>The items of a collection.</summary>
    /// <param name="key">Collection key.</param>
    /// <returns>The items, in broadcast order.</returns>
    IReadOnlyList<ContentEntry> Collection(string key);
}
