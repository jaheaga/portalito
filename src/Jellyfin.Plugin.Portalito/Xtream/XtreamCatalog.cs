using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Portalito.Catalog;

namespace Jellyfin.Plugin.Portalito.Xtream;

/// <summary>A movie matched on the IPTV source: the stream id and its real container extension.</summary>
public sealed record XtreamMovieMatch(int StreamId, string? ContainerExtension);

/// <summary>An episode matched on the IPTV source: the id that builds its stream URL and its container extension.</summary>
public sealed record XtreamEpisodeMatch(string EpisodeId, string? ContainerExtension);

/// <summary>Title lookup tables (shared shape for movies and series): exact title+year, and title -> all ids with it.</summary>
public sealed record XtreamTitleIndex<T>(
    IReadOnlyDictionary<string, T> ByTitleYear,
    IReadOnlyDictionary<string, IReadOnlyList<T>> ByTitle);

/// <summary>Both series lookups built from one fetch: by TMDB id (exact) and by normalized title+year.</summary>
public sealed record XtreamSeriesIndexes(IReadOnlyDictionary<int, int> ByTmdb, XtreamTitleIndex<int> ByName);

/// <summary>
/// Matches a portal title to the same title on the IPTV (Xtream) source, so the portal's own movies and series can play
/// from the IPTV account. Series match exactly by TMDB id (the series list carries it) or by normalized title+year when
/// only the name is known (episode playback); movies match by normalized title+year. The big indexes are built once and
/// cached; a concurrent burst shares one build (<see cref="SingleFlight"/>).
/// </summary>
public sealed class XtreamCatalog
{
    private const string SeriesKey = "series-index";
    private const string MovieKey = "movie-index";

    private readonly XtreamClient _client;
    private readonly SingleFlight _flights = new();
    private readonly TtlCache<XtreamSeriesIndexes> _series;
    private readonly TtlCache<XtreamTitleIndex<XtreamMovieMatch>> _movies;
    private readonly TtlCache<XtreamSeriesInfo> _seriesInfo;

    public XtreamCatalog(XtreamClient client, TimeProvider clock)
    {
        _client = client;
        _series = new TtlCache<XtreamSeriesIndexes>(clock, TimeSpan.FromHours(6), capacity: 1);
        _movies = new TtlCache<XtreamTitleIndex<XtreamMovieMatch>>(clock, TimeSpan.FromHours(6), capacity: 1);
        _seriesInfo = new TtlCache<XtreamSeriesInfo>(clock, TimeSpan.FromMinutes(30), capacity: 256);
    }

    /// <summary>Gets the underlying client (for health checks and building stream URLs).</summary>
    public XtreamClient Client => _client;

    /// <summary>Gets the configured base URL.</summary>
    public string BaseUrl => _client.BaseUrl;

    /// <summary>Signs in / reads account info; the health check the config page and source-health task use.</summary>
    public Task<XtreamAuth> AuthAsync(CancellationToken cancellationToken) => _client.AuthAsync(cancellationToken);

    /// <summary>Finds the IPTV series id for a TMDB id, or <c>null</c> when the source doesn't carry that series.</summary>
    public async Task<int?> FindSeriesIdByTmdbAsync(int tmdbId, CancellationToken cancellationToken)
    {
        var index = await SeriesIndexesAsync(cancellationToken).ConfigureAwait(false);
        return index.ByTmdb.TryGetValue(tmdbId, out var seriesId) ? seriesId : null;
    }

    /// <summary>Finds the IPTV series id for a show's name (and year, when known), or <c>null</c>. Used at episode playback.</summary>
    public async Task<int?> FindSeriesIdByNameAsync(string name, int? year, CancellationToken cancellationToken)
    {
        var index = await SeriesIndexesAsync(cancellationToken).ConfigureAwait(false);
        return MatchTitle(index.ByName, name, year);
    }

    /// <summary>
    /// Finds the IPTV episode for a series' TMDB id and a season/episode number, or <c>null</c> when the source lacks
    /// the series or that episode.
    /// </summary>
    public async Task<XtreamEpisodeMatch?> FindEpisodeByTmdbAsync(int seriesTmdbId, int season, int episode, CancellationToken cancellationToken)
    {
        var seriesId = await FindSeriesIdByTmdbAsync(seriesTmdbId, cancellationToken).ConfigureAwait(false);
        return seriesId is { } id ? await FindEpisodeAsync(id, season, episode, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>Finds the IPTV episode for a show's name/year and a season/episode number, or <c>null</c>.</summary>
    public async Task<XtreamEpisodeMatch?> FindEpisodeByNameAsync(string name, int? year, int season, int episode, CancellationToken cancellationToken)
    {
        var seriesId = await FindSeriesIdByNameAsync(name, year, cancellationToken).ConfigureAwait(false);
        return seriesId is { } id ? await FindEpisodeAsync(id, season, episode, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>All episodes of an IPTV series (every season), for browsing a show's seasons and episodes.</summary>
    public async Task<IReadOnlyList<XtreamEpisode>> EpisodesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var info = await SeriesInfoAsync(seriesId, cancellationToken).ConfigureAwait(false);
        return info.Episodes;
    }

    /// <summary>One IPTV episode by its id (for playback: gives the container extension), or null when it's gone.</summary>
    public async Task<XtreamEpisode?> EpisodeByIdAsync(int seriesId, string episodeId, CancellationToken cancellationToken)
    {
        var info = await SeriesInfoAsync(seriesId, cancellationToken).ConfigureAwait(false);
        foreach (var e in info.Episodes)
        {
            if (e.Id == episodeId)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>Finds the IPTV episode for a known IPTV series id and a season/episode number.</summary>
    public async Task<XtreamEpisodeMatch?> FindEpisodeAsync(int seriesId, int season, int episode, CancellationToken cancellationToken)
    {
        var info = await SeriesInfoAsync(seriesId, cancellationToken).ConfigureAwait(false);
        foreach (var e in info.Episodes)
        {
            if (e.Season == season && e.Episode == episode)
            {
                return new XtreamEpisodeMatch(e.Id, e.ContainerExtension);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the IPTV movie for a portal title. Matches on normalized title + year; when the year is unknown or no
    /// exact pair matches, a title that is unique across the source still matches. <c>null</c> when nothing does.
    /// </summary>
    public async Task<XtreamMovieMatch?> FindMovieAsync(string title, int? year, CancellationToken cancellationToken)
    {
        var index = await MovieIndexAsync(cancellationToken).ConfigureAwait(false);
        return MatchTitle(index, title, year);
    }

    /// <summary>
    /// Confirms a matched movie really is the expected TMDB title (one extra <c>get_vod_info</c> call), and returns its
    /// real container extension. <c>true</c> when the stream's TMDB id matches; <c>false</c> when it is known and differs;
    /// <c>null</c> when the source doesn't report one (then the title+year match stands).
    /// </summary>
    public async Task<(bool? Confirmed, string? ContainerExtension)> ConfirmMovieTmdbAsync(int streamId, int expectedTmdbId, CancellationToken cancellationToken)
    {
        var info = await _client.GetVodInfoAsync(streamId, cancellationToken).ConfigureAwait(false);
        if (info?.TmdbId is not { } tmdb)
        {
            return (null, info?.ContainerExtension);
        }

        return (tmdb == expectedTmdbId, info.ContainerExtension);
    }

    /// <summary>The title-match rule, over an already-built index (the testable core): exact title+year, else a unique title.</summary>
    internal static T? MatchTitle<T>(XtreamTitleIndex<T> index, string title, int? year)
        where T : class
    {
        var normalized = NormalizeTitle(title);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (year is { } y && index.ByTitleYear.TryGetValue(TitleYearKey(normalized, y), out var exact))
        {
            return exact;
        }

        return index.ByTitle.TryGetValue(normalized, out var sameTitle) && sameTitle.Count == 1 ? sameTitle[0] : null;
    }

    /// <summary>The title-match rule for a value type id (series). Same logic as <see cref="MatchTitle{T}"/>.</summary>
    internal static int? MatchTitle(XtreamTitleIndex<int> index, string title, int? year)
    {
        var normalized = NormalizeTitle(title);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (year is { } y && index.ByTitleYear.TryGetValue(TitleYearKey(normalized, y), out var exact))
        {
            return exact;
        }

        return index.ByTitle.TryGetValue(normalized, out var sameTitle) && sameTitle.Count == 1 ? sameTitle[0] : null;
    }

    /// <summary>Builds both series indexes (TMDB and title) from the bulk series list (the testable core).</summary>
    internal static XtreamSeriesIndexes BuildSeriesIndexes(IReadOnlyList<XtreamSeriesItem> series)
    {
        var byTmdb = new Dictionary<int, int>(series.Count);
        var byTitleYear = new Dictionary<string, int>(series.Count);
        var byTitle = new Dictionary<string, List<int>>();
        foreach (var s in series)
        {
            // Keep the first id for a TMDB id; later duplicates (re-uploads) are ignored.
            if (s.TmdbId is { } tmdb && tmdb > 0)
            {
                byTmdb.TryAdd(tmdb, s.SeriesId);
            }

            var normalized = NormalizeTitle(s.Name);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (s.Year is { } y)
            {
                byTitleYear.TryAdd(TitleYearKey(normalized, y), s.SeriesId);
            }

            if (!byTitle.TryGetValue(normalized, out var list))
            {
                byTitle[normalized] = list = new List<int>();
            }

            list.Add(s.SeriesId);
        }

        var titleIndex = new XtreamTitleIndex<int>(byTitleYear, byTitle.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value));
        return new XtreamSeriesIndexes(byTmdb, titleIndex);
    }

    /// <summary>Builds the movie lookup tables from the bulk VOD list (the testable core).</summary>
    internal static XtreamTitleIndex<XtreamMovieMatch> BuildMovieIndex(IReadOnlyList<XtreamVodItem> movies)
    {
        var byTitleYear = new Dictionary<string, XtreamMovieMatch>(movies.Count);
        var byTitle = new Dictionary<string, List<XtreamMovieMatch>>();
        foreach (var m in movies)
        {
            var normalized = NormalizeTitle(m.Name);
            if (normalized.Length == 0)
            {
                continue;
            }

            var match = new XtreamMovieMatch(m.StreamId, m.ContainerExtension);
            if (XtreamClient.YearOf(null, m.Name) is { } y)
            {
                byTitleYear.TryAdd(TitleYearKey(normalized, y), match);
            }

            if (!byTitle.TryGetValue(normalized, out var list))
            {
                byTitle[normalized] = list = new List<XtreamMovieMatch>();
            }

            list.Add(match);
        }

        return new XtreamTitleIndex<XtreamMovieMatch>(byTitleYear, byTitle.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<XtreamMovieMatch>)kv.Value));
    }

    private Task<XtreamSeriesIndexes> SeriesIndexesAsync(CancellationToken cancellationToken)
    {
        if (_series.TryGet(SeriesKey, out var cached))
        {
            return Task.FromResult(cached);
        }

        return _flights.RunAsync(SeriesKey, async () =>
        {
            var series = await _client.GetSeriesAsync(cancellationToken).ConfigureAwait(false);
            var result = BuildSeriesIndexes(series);
            _series.Set(SeriesKey, result);
            return result;
        }, cancellationToken);
    }

    private Task<XtreamTitleIndex<XtreamMovieMatch>> MovieIndexAsync(CancellationToken cancellationToken)
    {
        if (_movies.TryGet(MovieKey, out var cached))
        {
            return Task.FromResult(cached);
        }

        return _flights.RunAsync(MovieKey, async () =>
        {
            var movies = await _client.GetVodStreamsAsync(cancellationToken).ConfigureAwait(false);
            var result = BuildMovieIndex(movies);
            _movies.Set(MovieKey, result);
            return result;
        }, cancellationToken);
    }

    private Task<XtreamSeriesInfo> SeriesInfoAsync(int seriesId, CancellationToken cancellationToken)
    {
        var key = seriesId.ToString(CultureInfo.InvariantCulture);
        if (_seriesInfo.TryGet(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        return _flights.RunAsync("info:" + key, async () =>
        {
            var info = await _client.GetSeriesInfoAsync(seriesId, cancellationToken).ConfigureAwait(false);
            _seriesInfo.Set(key, info);
            return info;
        }, cancellationToken);
    }

    private static string TitleYearKey(string normalizedTitle, int year)
        => normalizedTitle + "|" + year.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Normalizes a title for matching: drops a trailing <c>(year)</c>, strips accents, lowercases, and reduces to
    /// single-spaced alphanumeric words. "Amélie (2001)" and "amelie" both become "amelie".
    /// </summary>
    internal static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var withoutYear = Regex.Replace(title, @"\s*\((19|20)\d{2}\)\s*$", string.Empty);
        var decomposed = withoutYear.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }
}
