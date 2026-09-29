namespace Jellyfin.Plugin.Portalito.Collage;

/// <summary>
/// One image a collage can show. <see cref="Key"/> names what it shows, so two seasons of one series or numbered
/// channels of one show ("The Simpsons T3"/"T5", "La Casa de los Famosos 1".."4") count as the same thing even
/// though their image URLs differ.
/// </summary>
public sealed record CollageImage(string Key, string Url)
{
    /// <summary>A title reduced to its letters, lowercased, without quality tags: "La Casa de los Famosos 4 HD" and
    /// "La casa de los famosos 1" -> "lacasadelosfamosos". Strip any season marker before calling.</summary>
    public static string KeyFor(string title)
    {
        var words = title.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("hd" or "sd" or "fhd" or "uhd" or "4k"));
        var key = new string(string.Concat(words).Where(char.IsLetter).ToArray());
        return key.Length > 0 ? key : title;
    }
}

/// <summary>
/// Picks which images each folder's collage shows so sibling and parent folders don't all repeat the same newest
/// titles (owner's rule 2026-09-24: "the more detailed the category, first; the most general, later"). Folders are
/// served in tiers from most specific to most general, sharing one set of shows already used: each takes its best
/// candidates nobody has taken yet, and only when it runs out tops up with its own best ones again, so no collage is
/// short. Within one collage a show appears once.
/// </summary>
public static class CollagePlanner
{
    /// <summary>Images per collage (the 2x2 grid).</summary>
    public const int PerCollage = 4;

    /// <summary>One folder's pick from <paramref name="candidates"/> (best first), recorded in <paramref name="used"/>.</summary>
    public static IReadOnlyList<string> Pick(IReadOnlyList<CollageImage> candidates, ISet<string> used)
    {
        var shows = candidates.DistinctBy(c => c.Key, StringComparer.Ordinal).ToList();
        var picked = shows.Where(c => !used.Contains(c.Key)).Take(PerCollage).ToList();
        picked.AddRange(shows.Where(c => !picked.Contains(c)).Take(PerCollage - picked.Count));

        used.UnionWith(picked.Select(c => c.Key));
        return picked.Select(c => c.Url).ToList();
    }

    /// <summary>
    /// Picks for a tier of sibling folders, most specific first (<see cref="MostSpecificFirst"/>); the result is in
    /// the input's order.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> PickAll(IReadOnlyList<IReadOnlyList<CollageImage>> candidates, ISet<string> used)
    {
        var picks = new IReadOnlyList<string>[candidates.Count];
        foreach (var index in MostSpecificFirst(candidates))
        {
            picks[index] = Pick(candidates[index], used);
        }

        return picks;
    }

    /// <summary>
    /// Sibling indexes ordered from most to least specific. A folder is general when its shows turn up across many
    /// different siblings (anime's "Animación" overlaps every other anime genre) and specific when it shares with few
    /// ("Western"); among equals, the one whose shows are shared less often goes first. The portal gives no
    /// per-filter totals, so overlap is the measure. Ties keep their order; folders with no candidates go last.
    /// </summary>
    public static IReadOnlyList<int> MostSpecificFirst(IReadOnlyList<IReadOnlyList<CollageImage>> candidates)
    {
        var sets = candidates.Select(c => c.Select(i => i.Key).ToHashSet(StringComparer.Ordinal)).ToList();
        var folderCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in sets.SelectMany(s => s))
        {
            folderCount[key] = folderCount.GetValueOrDefault(key) + 1;
        }

        int SiblingsOverlapped(int i) => sets[i].Count == 0 ? int.MaxValue : Enumerable.Range(0, sets.Count).Count(j => j != i && sets[i].Overlaps(sets[j]));
        double MeanSharing(int i) => sets[i].Count == 0 ? double.MaxValue : sets[i].Average(k => folderCount[k] - 1.0);

        return Enumerable.Range(0, candidates.Count).OrderBy(SiblingsOverlapped).ThenBy(MeanSharing).ThenBy(i => i).ToList();
    }

    /// <summary>The lists' items round-robin (first of each, then second of each...), for a parent drawing from several children.</summary>
    public static IReadOnlyList<T> Interleave<T>(IReadOnlyList<IReadOnlyList<T>> lists)
    {
        var result = new List<T>();
        for (var i = 0; lists.Any(l => i < l.Count); i++)
        {
            result.AddRange(lists.Where(l => i < l.Count).Select(l => l[i]));
        }

        return result;
    }
}
