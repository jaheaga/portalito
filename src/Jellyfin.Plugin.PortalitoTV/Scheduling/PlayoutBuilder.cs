using System.Globalization;
using Jellyfin.Plugin.PortalitoTV.Configuration;
using Jellyfin.Plugin.PortalitoTV.Media;

namespace Jellyfin.Plugin.PortalitoTV.Scheduling;

/// <summary>
/// Builds a channel's grid. Pure logic (no Jellyfin) so it can be tested. Schedule mode: each block starts at its (local) time
/// and is filled with whole episodes of its series while they fit; the last one that doesn't fit is left for next time and the
/// gap is covered with cut filler (so the next block starts exactly on time). Shuffle mode: a continuous random mix.
/// </summary>
public sealed class PlayoutBuilder
{
    /// <summary>The collection key of the favorite movies.</summary>
    public const string MoviesKey = "movies";

    /// <summary>Gaps shorter than this get no filler (the last program is stretched).</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);

    private readonly ChannelSettings _channel;
    private readonly TimeZoneInfo _tz;
    private readonly IContentLookup _content;
    private readonly Random _rng;

    /// <summary>Initializes a new instance of the <see cref="PlayoutBuilder"/> class.</summary>
    /// <param name="channel">The channel settings.</param>
    /// <param name="tz">The time zone block start times are read in.</param>
    /// <param name="content">Where the content collections come from.</param>
    /// <param name="rng">Random source (a fixed one in tests).</param>
    public PlayoutBuilder(ChannelSettings channel, TimeZoneInfo tz, IContentLookup content, Random? rng = null)
    {
        _channel = channel;
        _tz = tz;
        _content = content;
        _rng = rng ?? Random.Shared;
    }

    /// <summary>Gets the key of the filler collection (Schedule) or of the whole channel (Shuffle).</summary>
    public string MixKey
    {
        get
        {
            var keys = _channel.FillerSeries.Select(SeriesKey).ToList();
            if (_channel.IncludeFavoriteMovies)
            {
                keys.Add(MoviesKey);
            }

            return keys.Count == 1 ? keys[0] : "mix:" + string.Join('|', keys);
        }
    }

    /// <summary>The collection key of a series.</summary>
    /// <param name="portalitoId">The series' Portalito id.</param>
    /// <returns>The key.</returns>
    public static string SeriesKey(string portalitoId) => "series:" + portalitoId;

    /// <summary>The collection of a single movie (a block that airs that movie once).</summary>
    /// <param name="itemId">The movie's Jellyfin id.</param>
    /// <returns>The key.</returns>
    public static string MovieKey(Guid itemId) => "movie:" + itemId.ToString("N");

    /// <summary>
    /// The collection that feeds a block. <c>BlockSettings.SeriesId</c> is a series' PortalitoId, "movies" (a random hearted
    /// movie each time) or "movie:&lt;id&gt;" (that specific movie).
    /// </summary>
    /// <param name="id">The block's <c>SeriesId</c>.</param>
    /// <returns>The collection key.</returns>
    public static string BlockKey(string id) =>
        id == MoviesKey || id.StartsWith("movie:", StringComparison.Ordinal) ? id : SeriesKey(id);

    /// <summary>
    /// Drops what is scheduled after the program on air (to reprogram after a configuration change) and gives those items back
    /// to their collections, as if they had not aired.
    /// </summary>
    /// <param name="playout">The channel's grid.</param>
    /// <param name="nowUtc">The current time.</param>
    public static void TruncateAfterCurrent(Playout playout, DateTime nowUtc)
    {
        var keep = playout.Items.FindLastIndex(i => i.StartUtc <= nowUtc);

        // A multi-part episode isn't cut in the middle: if a part is on air, the parts that follow it are kept.
        while (keep >= 0 && keep + 1 < playout.Items.Count && playout.Items[keep].PartGroup is { } group
               && playout.Items[keep + 1].PartGroup == group && playout.Items[keep + 1].PartIndex > playout.Items[keep].PartIndex)
        {
            keep++;
        }

        var removed = playout.Items.Skip(keep + 1).ToList();
        playout.Items.RemoveRange(keep + 1, removed.Count);
        playout.BuiltUntilUtc = keep >= 0 ? playout.Items[keep].EndUtc : nowUtc;

        // Give them back in reverse order so the first dropped one ends up first in the bag.
        foreach (var item in Enumerable.Reverse(removed))
        {
            // Parts 2, 3... don't go back to the bag by themselves: the first part brings them (they are scheduled together).
            if (item.CollectionKey is null || item.IsSlate || item.PartIndex > 1)
            {
                continue;
            }

            var state = playout.State(item.CollectionKey);
            state.Bag.Insert(0, item.ItemId);
            if (item.Season is { } s && item.Episode is { } e)
            {
                // Chronological: resume from the first dropped episode.
                state.NextSeason = s;
                state.NextEpisode = e;
            }
        }
    }

    /// <summary>Removes what ended more than <paramref name="keep"/> ago.</summary>
    /// <param name="playout">The channel's grid.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="keep">How long finished programs are kept.</param>
    public static void Prune(Playout playout, DateTime nowUtc, TimeSpan keep) =>
        playout.Items.RemoveAll(i => i.EndUtc < nowUtc - keep);

    /// <summary>Extends the grid up to <paramref name="untilUtc"/>. If it is empty, it starts at <paramref name="nowUtc"/>.</summary>
    /// <param name="playout">The channel's grid.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="untilUtc">How far to build.</param>
    public void Extend(Playout playout, DateTime nowUtc, DateTime untilUtc)
    {
        if (playout.Items.Count == 0 || playout.BuiltUntilUtc < nowUtc)
        {
            // Empty or expired grid (the server was off): it starts over from now.
            playout.Items.Clear();
            playout.BuiltUntilUtc = nowUtc;
        }

        while (playout.BuiltUntilUtc < untilUtc)
        {
            var before = playout.BuiltUntilUtc;
            if (_channel.Mode == ChannelMode.Shuffle)
            {
                FillShuffle(playout, untilUtc);
            }
            else
            {
                FillNextBlock(playout);
            }

            if (playout.BuiltUntilUtc <= before)
            {
                break; // nothing to schedule
            }
        }
    }

    /// <summary>The block in force at <paramref name="utc"/>, when it started and the time (UTC) the next one starts.</summary>
    /// <param name="utc">The moment.</param>
    /// <returns>The block (null if the channel has none), its start and the next block's start.</returns>
    public (BlockSettings? Block, DateTime StartUtc, DateTime NextStartUtc) BlockAt(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, _tz).Date;
        var starts = new List<(DateTime Utc, BlockSettings Block)>();
        for (int d = -1; d <= 2; d++)
        {
            var day = local.AddDays(d);
            foreach (var b in _channel.Blocks)
            {
                if (b.Days.Count > 0 && !b.Days.Contains((int)day.DayOfWeek))
                {
                    continue;
                }

                if (!TimeSpan.TryParseExact(b.Start, @"hh\:mm", CultureInfo.InvariantCulture, out var at))
                {
                    continue;
                }

                var startLocal = DateTime.SpecifyKind(day + at, DateTimeKind.Unspecified);
                starts.Add((TimeZoneInfo.ConvertTimeToUtc(startLocal, _tz), b));
            }
        }

        // At the same time, a block for specific days ("Mondays only") wins over the every-day one.
        starts = starts.GroupBy(s => s.Utc)
            .Select(g => g.OrderByDescending(s => s.Block.Days.Count > 0).First())
            .OrderBy(s => s.Utc)
            .ToList();
        var current = starts.LastOrDefault(s => s.Utc <= utc);
        var next = starts.FirstOrDefault(s => s.Utc > utc);
        var nextUtc = next.Block is null ? utc.AddHours(24) : next.Utc;
        return (current.Block, current.Utc, nextUtc);
    }

    private static void Add(Playout playout, ContentEntry? entry, string? key, DateTime start, DateTime end, bool filler)
    {
        var item = entry is null
            ? new PlayoutItem(Guid.Empty, start, end, TimeSpan.Zero, EmitLanguage.Auto, "Portalito TV", IsFiller: true)
            : entry.Template with
            {
                StartUtc = start,
                EndUtc = end,
                InPoint = TimeSpan.Zero,
                CollectionKey = key,
                IsFiller = filler,

                // Filler is cut and ignores parts: it carries no group data.
                PartGroup = filler ? null : entry.Template.PartGroup,
                PartIndex = filler ? 0 : entry.Template.PartIndex,
                PartCount = filler ? 0 : entry.Template.PartCount,
            };
        playout.Items.Add(item);
        playout.BuiltUntilUtc = end;
    }

    private void FillShuffle(Playout playout, DateTime untilUtc)
    {
        var key = MixKey;
        var t = playout.BuiltUntilUtc;
        while (t < untilUtc)
        {
            var entry = NextShuffle(playout, key, consume: true, grouped: true);
            if (entry is null)
            {
                return;
            }

            // A multi-part episode airs whole and in order.
            foreach (var part in PartsOf(key, entry))
            {
                Add(playout, part, key, t, t + part.Duration, filler: false);
                t += part.Duration;
            }
        }
    }

    /// <summary>
    /// The parts of a multi-part episode ("primera parte", "(1)"...) in order, starting at <paramref name="entry"/> (which must
    /// be the first). Any other item goes alone.
    /// </summary>
    private List<ContentEntry> PartsOf(string key, ContentEntry entry)
    {
        var p = entry.Template;
        if (p.PartGroup is null || p.PartIndex != 1 || p.PartCount < 2)
        {
            return new List<ContentEntry> { entry };
        }

        var parts = _content.Collection(key).Where(e => e.Template.PartGroup == p.PartGroup).OrderBy(e => e.Template.PartIndex).ToList();
        return parts.Count == p.PartCount && parts[0].ItemId == entry.ItemId ? parts : new List<ContentEntry> { entry };
    }

    private void FillNextBlock(Playout playout)
    {
        var t = playout.BuiltUntilUtc;
        var (block, blockStart, nextStart) = BlockAt(t);
        var end = nextStart;
        if (block is not null && BlockEnd(block, blockStart) is { } blockEnd && blockEnd < nextStart)
        {
            if (t >= blockEnd)
            {
                // After the block's end time: filler up to the next block.
                FillGap(playout, t, nextStart);
                return;
            }

            end = blockEnd;
        }

        if (block is not null)
        {
            var key = BlockKey(block.SeriesId);

            // Movies have no season/episode: always random. A specific movie airs only once.
            var chronological = block.Order == BlockOrder.Chronological && !key.StartsWith("movie", StringComparison.Ordinal);
            var single = key.StartsWith("movie:", StringComparison.Ordinal);
            while (true)
            {
                var entry = chronological
                    ? NextChronological(playout, key, consume: false, grouped: true)
                    : NextShuffle(playout, key, consume: false, grouped: true);
                if (entry is null)
                {
                    break;
                }

                // Multi-part episodes fit whole in the block or not at all (they aren't split).
                var unit = PartsOf(key, entry);
                var total = unit.Aggregate(TimeSpan.Zero, (sum, part) => sum + part.Duration);
                if (t + total > end)
                {
                    break;
                }

                if (chronological)
                {
                    NextChronological(playout, key, consume: true, grouped: true);
                    if (unit.Count > 1)
                    {
                        var state = playout.State(key);
                        state.NextSeason = unit[^1].Season;
                        state.NextEpisode = unit[^1].Episode + 1;
                    }
                }
                else
                {
                    NextShuffle(playout, key, consume: true, grouped: true);
                }

                foreach (var part in unit)
                {
                    Add(playout, part, key, t, t + part.Duration, filler: false);
                    t += part.Duration;
                }

                if (single)
                {
                    break;
                }
            }
        }

        FillGap(playout, t, end);
    }

    /// <summary>Covers [t, end) with cut filler (or a slate if there is no filler).</summary>
    private void FillGap(Playout playout, DateTime t, DateTime end)
    {
        if (end - t < MinGap)
        {
            if (playout.Items.Count > 0 && playout.Items[^1].EndUtc == t && t < end)
            {
                playout.Items[^1] = playout.Items[^1] with { EndUtc = end };
            }

            playout.BuiltUntilUtc = end;
            return;
        }

        var key = MixKey;
        while (end - t >= MinGap)
        {
            var entry = _channel.FillerSeries.Count > 0 || _channel.IncludeFavoriteMovies
                ? NextShuffle(playout, key, consume: true)
                : null;
            if (entry is null)
            {
                Add(playout, null, null, t, end, filler: true);
                t = end;
                break;
            }

            var stop = t + entry.Duration < end ? t + entry.Duration : end;
            Add(playout, entry, key, t, stop, filler: true);
            t = stop;
        }

        if (t < end && playout.Items.Count > 0)
        {
            playout.Items[^1] = playout.Items[^1] with { EndUtc = end };
        }

        playout.BuiltUntilUtc = end;
    }

    /// <param name="grouped">Multi-part episodes enter as a unit: only the first part is chosen (the others follow it). Filler doesn't group.</param>
    private ContentEntry? NextShuffle(Playout playout, string key, bool consume, bool grouped = false)
    {
        var all = grouped ? _content.Collection(key).Where(e => e.Template.PartIndex <= 1).ToList() : _content.Collection(key);
        if (all.Count == 0)
        {
            return null;
        }

        var byId = all.ToDictionary(e => e.ItemId);
        var state = playout.State(key);

        // Skip what is no longer there (unchecked season, removed heart).
        while (state.Bag.Count > 0 && !byId.ContainsKey(state.Bag[0]))
        {
            state.Bag.RemoveAt(0);
        }

        if (state.Bag.Count == 0)
        {
            var ids = all.Select(e => e.ItemId).ToList();
            for (int i = ids.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (ids[i], ids[j]) = (ids[j], ids[i]);
            }

            if (ids.Count > 1 && ids[0] == state.LastPlayed)
            {
                (ids[0], ids[^1]) = (ids[^1], ids[0]);
            }

            state.Bag = ids;
        }

        var entry = byId[state.Bag[0]];
        if (consume)
        {
            state.Bag.RemoveAt(0);
            state.LastPlayed = entry.ItemId;
        }

        return entry;
    }

    private ContentEntry? NextChronological(Playout playout, string key, bool consume, bool grouped = false)
    {
        var all = grouped ? _content.Collection(key).Where(e => e.Template.PartIndex <= 1).ToList() : _content.Collection(key);
        if (all.Count == 0)
        {
            return null;
        }

        var state = playout.State(key);
        var ordered = all.OrderBy(e => e.Season).ThenBy(e => e.Episode).ToList();
        var entry = ordered.FirstOrDefault(e => (e.Season, e.Episode).CompareTo((state.NextSeason, state.NextEpisode)) >= 0)
                    ?? ordered[0]; // end of the series: back to the start
        if (consume)
        {
            state.NextSeason = entry.Season;
            state.NextEpisode = entry.Episode + 1;
            state.LastPlayed = entry.ItemId;
        }

        return entry;
    }

    /// <summary>The end (UTC) of an occurrence of the block, if it has an end time.</summary>
    private DateTime? BlockEnd(BlockSettings block, DateTime blockStartUtc)
    {
        if (string.IsNullOrWhiteSpace(block.End)
            || !TimeSpan.TryParseExact(block.End, @"hh\:mm", CultureInfo.InvariantCulture, out var endAt)
            || !TimeSpan.TryParseExact(block.Start, @"hh\:mm", CultureInfo.InvariantCulture, out var startAt))
        {
            return null;
        }

        var length = endAt - startAt;
        if (length <= TimeSpan.Zero)
        {
            length += TimeSpan.FromDays(1); // ends the next day (e.g. 22:00 to 00:30)
        }

        // Computed in local time to respect daylight-saving changes.
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(blockStartUtc, _tz);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart + length, DateTimeKind.Unspecified), _tz);
    }
}
