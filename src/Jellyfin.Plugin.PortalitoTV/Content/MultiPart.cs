using System.Text.RegularExpressions;
using Jellyfin.Plugin.PortalitoTV.Scheduling;

namespace Jellyfin.Plugin.PortalitoTV.Content;

/// <summary>
/// Detects multi-part episodes by their title ("(primera parte)", "Parte 2", "(1)", "Primera parte de la trilogía ...") and
/// groups them so they are scheduled together and in order. Pure logic (no Jellyfin) so it can be tested.
/// </summary>
public static partial class MultiPart
{
    private const int MaxParts = 5;

    /// <summary>A part marker found in a title.</summary>
    /// <param name="Part">Part number (1 = first).</param>
    /// <param name="Base">The title without the marker, normalized (only letters and digits, lower case).</param>
    /// <param name="Parenthesized">The "(1)", "(2)" form: each part may then have a different name.</param>
    public sealed record Marker(int Part, string Base, bool Parenthesized);

    /// <summary>The title in lower case and only letters and digits, to compare names.</summary>
    /// <param name="title">The title.</param>
    /// <returns>The normalized title.</returns>
    public static string Normalize(string? title) =>
        new string((title ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>The part marker of a title, or null if it isn't a multi-part one.</summary>
    /// <param name="title">The episode title.</param>
    /// <returns>The marker, or null.</returns>
    public static Marker? Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var t = title.Trim();
        foreach (var (regex, parenthesized) in new[] { (Fraction(), true), (Paren(), true), (Ordinal(), false), (Numbered(), false) })
        {
            var m = regex.Match(t);
            if (!m.Success)
            {
                continue;
            }

            var part = WordToNumber(m.Groups[1].Value);
            if (part is < 1 or > MaxParts)
            {
                return null;
            }

            return new Marker(part, Normalize(t.Remove(m.Index, m.Length)), parenthesized);
        }

        return null;
    }

    /// <summary>
    /// Marks the episodes that are parts of one episode. The parts must be consecutive episodes (also from one season to the
    /// first of the next) numbered 1, 2, 3...; part 1 may carry no marker if part 2 repeats its title ("Name" /
    /// "Name - Parte 2"). Whatever doesn't fit stays a single episode.
    /// </summary>
    /// <param name="entries">The episodes of a series.</param>
    /// <param name="lastEpisode">Season → number of its last episode (to recognize the step from one season to the next).</param>
    /// <returns>The same episodes, in the same order, with the group data in <see cref="PlayoutItem.PartGroup"/>.</returns>
    public static List<ContentEntry> Assign(IReadOnlyList<ContentEntry> entries, IReadOnlyDictionary<int, int> lastEpisode)
    {
        var order = entries.Select((e, i) => (Entry: e, Index: i)).OrderBy(x => x.Entry.Season).ThenBy(x => x.Entry.Episode).ToList();
        var marks = order.Select(x => Parse(x.Entry.Template.EpisodeTitle)).ToList();
        var result = entries.ToList();
        var taken = new bool[order.Count];

        bool Consecutive(int a, int b)
        {
            var x = order[a].Entry;
            var y = order[b].Entry;
            return (y.Season == x.Season && y.Episode == x.Episode + 1)
                   || (y.Season == x.Season + 1 && y.Episode == 1 && lastEpisode.TryGetValue(x.Season, out var last) && x.Episode == last);
        }

        for (int i = 0; i < order.Count; i++)
        {
            if (taken[i])
            {
                continue;
            }

            int start;
            string baseTitle;
            bool paren;
            if (marks[i] is { Part: 1 } first)
            {
                start = i;
                baseTitle = first.Base;
                paren = first.Parenthesized;
            }
            else if (marks[i] is { Part: 2 } second && i > 0 && !taken[i - 1] && marks[i - 1] is null && Consecutive(i - 1, i)
                     && Normalize(order[i - 1].Entry.Template.EpisodeTitle) == second.Base && second.Base.Length > 0)
            {
                // Part 1 carries no marker but its title is part 2's without the marker.
                start = i - 1;
                baseTitle = second.Base;
                paren = false;
            }
            else
            {
                continue;
            }

            var run = new List<int> { start };
            int expected = 2;
            for (int j = start + 1; j < order.Count && run.Count < MaxParts && !taken[j] && Consecutive(j - 1, j)
                 && marks[j] is { } m && m.Part == expected && (m.Base == baseTitle || (m.Parenthesized && paren)); j++)
            {
                run.Add(j);
                expected++;
            }

            if (run.Count < 2)
            {
                continue;
            }

            var group = $"{order[start].Entry.Season}x{order[start].Entry.Episode}";
            for (int k = 0; k < run.Count; k++)
            {
                taken[run[k]] = true;
                var (entry, original) = order[run[k]];
                result[original] = entry with { Template = entry.Template with { PartGroup = group, PartIndex = k + 1, PartCount = run.Count } };
            }
        }

        return result;
    }

    // "(1/2)" -> part 1 of 2.
    [GeneratedRegex(@"\((\d)\s*/\s*\d\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Fraction();

    // "Title (1)" / "Other title (2)": a number in parentheses at the end.
    [GeneratedRegex(@"\((\d)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Paren();

    // "primera parte", "Segunda parte de la trilogía ...".
    [GeneratedRegex(@"\b(primera|segunda|tercera|cuarta|quinta)\s+parte\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Ordinal();

    // "Parte 2", "(Parte 1)", "Part Two", "Pt. 3". "parte del pueblo" doesn't count: it needs a number or a written number.
    [GeneratedRegex(@"\b(?:parte|part|pt\.?)\s*(\d|uno|dos|tres|cuatro|cinco|one|two|three|four|five)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Numbered();

    private static int WordToNumber(string w) => w.ToLowerInvariant() switch
    {
        "primera" or "uno" or "one" => 1,
        "segunda" or "dos" or "two" => 2,
        "tercera" or "tres" or "three" => 3,
        "cuarta" or "cuatro" or "four" => 4,
        "quinta" or "cinco" or "five" => 5,
        _ => int.TryParse(w, out var n) ? n : 0,
    };
}
