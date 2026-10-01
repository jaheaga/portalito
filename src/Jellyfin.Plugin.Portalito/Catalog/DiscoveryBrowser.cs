using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Metadata;
using Jellyfin.Plugin.Portalito.Portal;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>
/// "Destacado" rows built from TMDB lists (trending, popular, discover by country/genre...) and reconciled against the
/// portal: each TMDB title is searched for in the portal and kept only if the portal really has it, so every listed
/// title plays. What's listed follows TMDB's order; what plays is always the portal's own content.
/// </summary>
public sealed class DiscoveryBrowser
{
    /// <summary>At most this many portal titles per row.</summary>
    public const int RowCap = 60;

    /// <summary>Portal searches in flight at once while reconciling a row.</summary>
    public const int SearchConcurrency = 4;

    private static readonly HashSet<string> SeriesTypes = new(StringComparer.OrdinalIgnoreCase) { "teleplay", "series", "variety" };

    // A title the portal doesn't have is remembered too, so it isn't searched again on every opening.
    private static readonly JsonObject NoMatch = new();

    private readonly TmdbClient _tmdb;
    private readonly TtlCache<IReadOnlyList<JsonObject>> _rows;
    private readonly TtlCache<JsonObject> _titles;

    public DiscoveryBrowser(TmdbClient tmdb, TimeProvider clock, IReadOnlyList<FeaturedRow> rows)
    {
        _tmdb = tmdb;
        Rows = rows;
        _rows = new TtlCache<IReadOnlyList<JsonObject>>(clock, TimeSpan.FromHours(6), capacity: Math.Max(rows.Count, 1));
        _titles = new TtlCache<JsonObject>(clock, TimeSpan.FromDays(1), capacity: 5_000);
    }

    public IReadOnlyList<FeaturedRow> Rows { get; }

    /// <summary>
    /// A row's folder number, from its label: the same row keeps its id (and the image Jellyfin stored for it) wherever it
    /// sits in the list. Ids were the row's position until 0.1.1.3: inserting rows gave the rows after them the positions,
    /// and so the stored collages, of other rows (measured 2026-10-01: Disney+ showing "Películas de anime"). Always
    /// 100000000 or more, so it never equals an old position id.
    /// </summary>
    public static int RowKey(string label)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label.Trim()));
        return 100_000_000 + (int)(BitConverter.ToUInt32(hash, 0) % 900_000_000);
    }

    /// <summary>The index of the row whose <see cref="RowKey"/> is <paramref name="key"/>; -1 if no row has it (any more).</summary>
    public int IndexOfRow(int key)
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            if (RowKey(Rows[i].Label) == key)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Parses the configured rows: one per line or separated by <c>;</c>, each <c>Label | source | key=value ...</c>
    /// (the parameters are space-separated; list alternatives with commas, e.g. <c>country=JP,KR</c>). Rows with no
    /// label or an unknown source are skipped.
    /// </summary>
    public static IReadOnlyList<FeaturedRow> ParseRows(string? configured)
    {
        var rows = new List<FeaturedRow>();
        foreach (var raw in (configured ?? string.Empty).Split(new[] { ';', '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = raw.Split('|', 3, StringSplitOptions.TrimEntries);
            if (fields.Length < 2 || fields[0].Length == 0 || !TmdbLists.IsKnownSource(fields[1]))
            {
                continue;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fields.Length == 3)
            {
                foreach (var pair in fields[2].Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        parameters[pair[..eq]] = pair[(eq + 1)..];
                    }
                }
            }

            rows.Add(new FeaturedRow(fields[0], fields[1].ToLowerInvariant(), parameters));
        }

        return rows;
    }

    /// <summary>The row's TMDB titles (for its collage thumbnail; not yet reconciled with the portal).</summary>
    public Task<IReadOnlyList<TmdbEntry>> EntriesAsync(int rowIndex, CancellationToken cancellationToken)
        => _tmdb.ListAsync(Rows[rowIndex], cancellationToken);

    /// <summary>
    /// The row's portal titles: its TMDB list, each title searched in the portal and matched, unmatched ones dropped,
    /// TMDB's order kept, one entry per show, capped at <see cref="RowCap"/>. The items are raw portal assets, the same
    /// shape as a catalog listing.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> RowAsync(PortalClient portal, int rowIndex, CancellationToken cancellationToken)
    {
        var key = rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_rows.TryGet(key, out var cached))
        {
            return cached;
        }

        var entries = await _tmdb.ListAsync(Rows[rowIndex], cancellationToken).ConfigureAwait(false);
        var matches = new JsonObject?[entries.Count];
        using var gate = new SemaphoreSlim(SearchConcurrency);
        await Task.WhenAll(entries.Select(async (entry, i) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                matches[i] = await ResolveAsync(portal, entry, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var row = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            if (match is null || !seen.Add(ShowKey(match)))
            {
                continue;
            }

            row.Add(match);
            if (row.Count >= RowCap)
            {
                break;
            }
        }

        if (entries.Count > 0)
        {
            _rows.Set(key, row);
        }

        return row;
    }

    /// <summary>
    /// The portal asset that is the same title as <paramref name="entry"/>, or null. A candidate matches when it is the
    /// same kind (movie/series) and its name or alias -- season marker removed, normalized -- equals the entry's
    /// localized, English or original title. Movies must also be within a year; for a series the season closest to the
    /// show's first air date wins (a season's releaseTime is its own).
    /// </summary>
    public static JsonObject? PickMatch(IEnumerable<JsonObject> candidates, TmdbEntry entry)
    {
        var wanted = new[] { entry.Title, entry.EnglishTitle, entry.OriginalTitle }
            .Select(TmdbMatching.Normalize).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return null;
        }

        var matching = candidates
            .Where(c => SeriesTypes.Contains(PortalJson.Str(c["programType"]) ?? string.Empty) == entry.IsSeries)
            .Where(c => new[] { PortalJson.Str(c["name"]), PortalJson.Str(c["alias"]) }
                .Any(t => wanted.Contains(TmdbMatching.Normalize(SeasonGrouping.WithoutSeason(t)))))
            .Select(c => (Item: c, Year: PortalJson.Year(c["releaseTime"])))
            .ToList();

        if (!entry.IsSeries)
        {
            return entry.Year is { } year
                ? matching.FirstOrDefault(m => m.Year is { } y && Math.Abs(y - year) <= 1).Item
                : matching.FirstOrDefault().Item;
        }

        return entry.Year is { } showYear
            ? matching.OrderBy(m => m.Year is { } y ? Math.Abs(y - showYear) : int.MaxValue).FirstOrDefault().Item
            : matching.FirstOrDefault().Item;
    }

    /// <summary>The flattened items of a <c>v3/searchByName</c> answer (<c>searchItemList[].itemList[]</c>).</summary>
    public static IEnumerable<JsonObject> SearchItems(JsonObject payload)
        => PortalJson.Objects(payload["searchItemList"]).SelectMany(group => PortalJson.Objects(group["itemList"]));

    private async Task<JsonObject?> ResolveAsync(PortalClient portal, TmdbEntry entry, CancellationToken cancellationToken)
    {
        var key = (entry.IsSeries ? "tv:" : "movie:") + entry.Id;
        if (_titles.TryGet(key, out var cached))
        {
            return ReferenceEquals(cached, NoMatch) ? null : cached;
        }

        // Search by the Spanish title first, then the English and original ones -- whichever the portal filed it under.
        var searched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in new[] { entry.Title, entry.EnglishTitle, entry.OriginalTitle })
        {
            if (string.IsNullOrWhiteSpace(text) || !searched.Add(TmdbMatching.Normalize(text)))
            {
                continue;
            }

            PortalResponse response;
            try
            {
                response = await portal.SearchAsync(text, size: 20, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (PortalException)
            {
                return null; // transient: not remembered as a miss
            }

            if (!response.IsSuccess)
            {
                return null;
            }

            if (PickMatch(SearchItems(response.Data!), entry) is { } match)
            {
                _titles.Set(key, match);
                return match;
            }
        }

        _titles.Set(key, NoMatch);
        return null;
    }

    private static string ShowKey(JsonObject item)
        => PortalJson.Str(item["programType"]) is { } type && SeriesTypes.Contains(type)
            ? "show:" + SeasonGrouping.WithoutSeason(PortalJson.Str(item["name"]))
            : "movie:" + PortalJson.Str(item["contentId"]);
}
