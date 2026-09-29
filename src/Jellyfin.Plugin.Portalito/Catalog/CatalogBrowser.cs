using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Portal;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>One of the four fixed Portalito catalogs "Descubrir" offers, independent of the configured native
/// shelf root codes.</summary>
public sealed record CatalogEntry(string Code, string Label);

/// <summary>The filter vocabulary from <c>v3/filterGenre</c>: genre tags (English, translated for display via
/// <see cref="GenreLabels"/>) and the years the catalog spans.</summary>
public sealed record FilterVocabulary(IReadOnlyList<string> Tags, IReadOnlyList<int> Years);

/// <summary>
/// Browses the Portalito portal's own full catalog (30k+ movies, 21k+ series) instead of the 10-item home-shelf
/// carousels -- see PROGRESS.md's 2026-09-23 entry for how this was found live: <c>v3/filterByContent</c> is a
/// real paginated catalog (not the shelf carousels' <c>v3/getColumnContents</c>, which always returns empty), but
/// its <c>columnId</c> is a root's <c>parentId</c>, not any shelf's own <c>columnId</c>/<c>code</c> -- those only
/// ever come back with an empty channelList/assetList from <c>v3/getShelveData</c> when tried directly.
/// </summary>
public sealed class CatalogBrowser
{
    /// <summary>How many titles one filtered listing shows -- a single <c>filterByContent</c> call, since
    /// Jellyfin caches whatever a folder returns as the whole folder (no further paging back into the portal).
    /// pageSize=200 was confirmed live 2026-09-23 to be honoured in one call.</summary>
    public const int ListingSize = 200;

    /// <summary>Upper bound on a whole listing (<see cref="ListAllAsync"/>): 25 portal pages. The busiest year of the
    /// biggest catalog is well under this (30,839 movies over ~37 years, newest years heaviest).</summary>
    public const int WholeListingCap = 5000;

    /// <summary>How many newest pages "Mejor valoradas" ranks by score. The portal has no rating sort, so this is a
    /// best-of-recent over ~1000 newest titles (owner decision 2026-09-28), not the whole catalog.</summary>
    public const int TopRatedPages = 5;

    /// <summary>The VOD catalogs to browse, from config (see <see cref="ParseCatalogs"/>). Empty means none.</summary>
    public IReadOnlyList<CatalogEntry> Entries { get; }

    private readonly TtlCache<long> _parentIds;
    private readonly TtlCache<FilterVocabulary> _vocabulary;
    private readonly TtlCache<IReadOnlyList<Collage.CollageImage>> _posters;
    private readonly TtlCache<IReadOnlyList<JsonObject>> _topRated;

    public CatalogBrowser(TimeProvider clock, IReadOnlyList<CatalogEntry> catalogs)
    {
        Entries = catalogs;
        var count = Math.Max(1, catalogs.Count);

        // Parent ids and the filter vocabulary change essentially never; a day-long TTL just means a restart (or
        // 24h of uptime) re-derives them instead of them being hardcoded.
        _parentIds = new TtlCache<long>(clock, TimeSpan.FromHours(24), capacity: count);
        _vocabulary = new TtlCache<FilterVocabulary>(clock, TimeSpan.FromHours(24), capacity: 1);
        // A folder's newest titles shift slowly, so day-old collage posters are fine; sized for every catalog's
        // Todo + 24 genres + ~37 years.
        _posters = new TtlCache<IReadOnlyList<Collage.CollageImage>>(clock, TimeSpan.FromHours(24), capacity: 400);
        // The "Mejor valoradas" ranking is several portal pages; a day-long TTL keeps opening the row cheap.
        _topRated = new TtlCache<IReadOnlyList<JsonObject>>(clock, TimeSpan.FromHours(24), capacity: count);
    }

    /// <summary>Parses the config catalog list: <c>code:Label</c> pairs separated by commas or newlines.</summary>
    public static IReadOnlyList<CatalogEntry> ParseCatalogs(string? configured)
    {
        var entries = new List<CatalogEntry>();
        foreach (var raw in (configured ?? string.Empty).Split(new[] { ',', '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = raw.IndexOf(':');
            if (sep <= 0)
            {
                continue;
            }

            var code = raw[..sep].Trim();
            var label = raw[(sep + 1)..].Trim();
            if (code.Length > 0 && label.Length > 0)
            {
                entries.Add(new CatalogEntry(code, label));
            }
        }

        return entries;
    }

    /// <summary>The root's parent column id -- what <see cref="ListAsync"/> actually needs, derived from the
    /// root's first shelf via <c>getRecommendColumnContents</c> rather than hardcoded (measured live 2026-09-23:
    /// movies -> 76177, series -> 76178, kids -> 76783, anime -> 76180).</summary>
    public async Task<long> ParentIdAsync(PortalClient portal, string catalogCode, CancellationToken cancellationToken)
    {
        if (_parentIds.TryGet(catalogCode, out var cached))
        {
            return cached;
        }

        var shelves = (await portal.NextColumnsAsync(catalogCode, size: 1, cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var firstShelf = PortalJson.Objects(shelves["recommendList"]).FirstOrDefault()
            ?? throw new PortalException($"catalog '{catalogCode}' has no shelves to derive a parent id from");
        var shelfId = PortalJson.Int(firstShelf["columnId"])
            ?? throw new PortalException($"catalog '{catalogCode}''s first shelf has no numeric columnId");

        var recommend = (await portal.RecommendColumnContentsAsync(shelfId, cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var parentId = PortalJson.Int(PortalJson.Objects(recommend["columnList"]).FirstOrDefault()?["parentId"])
            ?? throw new PortalException($"catalog '{catalogCode}' shelf {shelfId} reported no parentId");

        _parentIds.Set(catalogCode, parentId);
        return parentId;
    }

    /// <summary>The genre/year filter vocabulary, shared across every catalog (measured live 2026-09-23: the same
    /// 24 tags and year range regardless of which catalog you're about to filter).</summary>
    public async Task<FilterVocabulary> VocabularyAsync(PortalClient portal, CancellationToken cancellationToken)
    {
        if (_vocabulary.TryGet("vocab", out var cached))
        {
            return cached;
        }

        var response = (await portal.FilterGenreAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var tags = (response["tags"] as JsonArray)?.Select(n => PortalJson.Str(n)).OfType<string>().ToArray() ?? Array.Empty<string>();
        var years = (response["year"] as JsonArray)?.Select(n => PortalJson.Str(n)).Select(y => int.TryParse(y, out var n) ? n : (int?)null).OfType<int>().ToArray() ?? Array.Empty<int>();
        var vocabulary = new FilterVocabulary(tags, years);

        _vocabulary.Set("vocab", vocabulary);
        return vocabulary;
    }

    /// <summary>
    /// One filtered listing's raw portal items (movies and one-item-per-season series entries), newest first --
    /// <paramref name="tag"/>/<paramref name="year"/> are mutually exclusive; both null/empty means unfiltered.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> ListAsync(
        PortalClient portal, string catalogCode, string? tag, int? year, CancellationToken cancellationToken)
    {
        var parentId = await ParentIdAsync(portal, catalogCode, cancellationToken).ConfigureAwait(false);
        var response = (await portal.FilterByContentAsync(
            parentId,
            tags: tag ?? string.Empty,
            year: year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            size: ListingSize,
            cancellationToken: cancellationToken).ConfigureAwait(false)).Require();

        return PortalJson.Objects(response["assetList"]).ToArray();
    }

    /// <summary>
    /// One catalog's best-rated recent titles: the newest <see cref="TopRatedPages"/> pages, deduped and sorted by
    /// <c>score</c> descending (ties keep the portal's newest-first order), capped at <see cref="ListingSize"/>. The
    /// portal offers no rating sort, so this is a best-of-recent, not a whole-catalog ranking. Cached a day.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> TopRatedAsync(PortalClient portal, string catalogCode, CancellationToken cancellationToken)
    {
        if (_topRated.TryGet(catalogCode, out var cached))
        {
            return cached;
        }

        var parentId = await ParentIdAsync(portal, catalogCode, cancellationToken).ConfigureAwait(false);
        var all = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= TopRatedPages; page++)
        {
            var response = (await portal.FilterByContentAsync(parentId, page: page, size: ListingSize, cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
            var items = PortalJson.Objects(response["assetList"]).ToArray();
            foreach (var item in items)
            {
                if (PortalJson.Str(item["contentId"]) is { } id && seen.Add(id))
                {
                    all.Add(item);
                }
            }

            if (items.Length < ListingSize)
            {
                break;
            }
        }

        static double Score(JsonObject item)
            => PortalJson.Str(item["score"]) is { } s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        var ranked = all.OrderByDescending(Score).Take(ListingSize).ToArray(); // OrderByDescending is stable: ties keep newest-first order

        _topRated.Set(catalogCode, ranked);
        return ranked;
    }

    /// <summary>
    /// Every title of one filtered listing, paged <see cref="ListingSize"/> at a time up to <see cref="WholeListingCap"/>
    /// -- used for the "Por año" folders, so a year shows all its titles (and the search index reaches them), not just
    /// its newest 200. Stops at the first short page, or as soon as a page brings nothing new: if the portal ever
    /// ignored pageNum and served the same page again, this ends instead of looping to the cap on duplicates.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> ListAllAsync(
        PortalClient portal, string catalogCode, string? tag, int? year, CancellationToken cancellationToken)
    {
        var parentId = await ParentIdAsync(portal, catalogCode, cancellationToken).ConfigureAwait(false);
        var all = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; all.Count < WholeListingCap; page++)
        {
            var response = (await portal.FilterByContentAsync(
                parentId,
                tags: tag ?? string.Empty,
                year: year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                page: page,
                size: ListingSize,
                cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
            var items = PortalJson.Objects(response["assetList"]).ToArray();
            var added = 0;
            foreach (var item in items)
            {
                if (PortalJson.Str(item["contentId"]) is { } id && seen.Add(id))
                {
                    all.Add(item);
                    added++;
                }
            }

            if (items.Length < ListingSize || added == 0)
            {
                break;
            }
        }

        return all;
    }

    /// <summary>How many poster candidates a folder offers its collage: enough spare that a general folder, picking
    /// after its more specific siblings (see <see cref="Collage.CollagePlanner"/>), still finds titles of its own.</summary>
    public const int CollageCandidates = 48;

    /// <summary>The same for the unfiltered catalog, which feeds "Todo", "Por año" and the catalog's own tile after
    /// all ~24 genres have taken theirs -- mostly from those same newest titles (measured 2026-09-24: with only 24,
    /// "Todo" repeated the genres' posters).</summary>
    public const int CatalogCollageCandidates = 96;

    /// <summary>
    /// Up to <see cref="CollageCandidates"/> posters of one folder's own newest titles (the catalog's, one genre's or
    /// one year's), best first, for its collage thumbnail: one small <c>filterByContent</c> page per folder, so every
    /// folder gets its own -- including genres and older years with nothing among the catalog's newest titles. Each is
    /// keyed by its show, so a series' seasons (one catalog entry each) count as one.
    /// </summary>
    public async Task<IReadOnlyList<Collage.CollageImage>> PostersAsync(PortalClient portal, string catalogCode, string? tag, int? year, CancellationToken cancellationToken)
    {
        var key = $"{catalogCode}|{tag}|{year}";
        if (_posters.TryGet(key, out var cached))
        {
            return cached;
        }

        var parentId = await ParentIdAsync(portal, catalogCode, cancellationToken).ConfigureAwait(false);
        var response = (await portal.FilterByContentAsync(
            parentId,
            tags: tag ?? string.Empty,
            year: year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            size: tag is null && year is null ? CatalogCollageCandidates : CollageCandidates,
            cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        var posters = PortalJson.Objects(response["assetList"])
            .Select(item => (Url: PortalJson.PosterUrl(item), Title: SeasonGrouping.WithoutSeason(PortalJson.Str(item["name"]))))
            .Where(p => p.Url is not null)
            .Select(p => new Collage.CollageImage(Collage.CollageImage.KeyFor(p.Title.Length > 0 ? p.Title : p.Url!), p.Url!))
            .DistinctBy(p => p.Url, StringComparer.Ordinal)
            .ToList();
        _posters.Set(key, posters);
        return posters;
    }
}
