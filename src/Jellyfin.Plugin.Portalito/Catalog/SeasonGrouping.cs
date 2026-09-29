using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>
/// Reads a season number out of a Portalito item's name, and groups items of the same show. Ported from the reference client's season-parsing logic.
/// </summary>
public static class SeasonGrouping
{
    // Verbatim port of the reference client's own SEASON regex. Only "T2", "Temp.2"/"Temp 2", "Temporada 2" and "S02" are
    // recognised -- no "Season"/"Saison"/"Stagione" and no reversed "2a Temporada" form: those don't exist in the
    // real source.
    private static readonly Regex SeasonPattern = new(
        @"\bT\s?(?<a>\d{1,2})\b|\bTemp\.?\s?(?<b>\d{1,2})\b|\bTemporada\s?(?<c>\d{1,2})\b|\bS(?<d>\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns the season number found in <paramref name="name"/>; 1 when none is found (an unmarked,
    /// single-season series) -- matches the reference client's <c>seasonFromName</c> exactly, including on a blank name.</summary>
    public static int SeasonFromName(string? name)
    {
        var match = SeasonPattern.Match(name ?? string.Empty);
        if (!match.Success)
        {
            return 1;
        }

        foreach (var groupName in new[] { "a", "b", "c", "d" })
        {
            var group = match.Groups[groupName];
            if (group.Success && int.TryParse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }
        }

        return 1;
    }

    /// <summary>
    /// Returns <paramref name="name"/> with its season marker (if any) removed, trimmed and lowercased -- a
    /// grouping key, not a display title (the reference client's own use: <c>withoutSeason</c> is only ever compared against
    /// itself, never shown).
    /// </summary>
    public static string WithoutSeason(string? name)
        => SeasonPattern.Replace(name ?? string.Empty, string.Empty).Trim().ToLowerInvariant();

    /// <summary>Same as <see cref="WithoutSeason"/> but keeping the original case -- for showing a show's name,
    /// not for using as a grouping key.</summary>
    public static string StripSeasonForDisplay(string? name)
        => SeasonPattern.Replace(name ?? string.Empty, string.Empty).Trim();
}
