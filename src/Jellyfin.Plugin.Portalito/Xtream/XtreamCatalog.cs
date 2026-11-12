using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Portalito.Catalog;

namespace Jellyfin.Plugin.Portalito.Xtream;

/// <summary>A movie matched on the IPTV source: the stream id and its real container extension.</summary>
public sealed record XtreamMovieMatch(int StreamId, string? ContainerExtension);

/// <summary>An episode matched on the IPTV source: the id that builds its stream URL and its container extension.</summary>
public sealed record XtreamEpisodeMatch(string EpisodeId, string? ContainerExtension);

/// <summary>The movie lookup tables, built once from the bulk VOD list and cached.</summary>
public sealed record XtreamMovieIndex(
    IReadOnlyDictionary<string, XtreamMovieMatch> ByTitleYear,
    IReadOnlyDictionary<string, IReadOnlyList<XtreamMovieMatch>> ByTitle);

/// <summary>
/// Matches a portal title to the same title on the IPTV (Xtream) source, so the portal's own movies and series can play
/// from the IPTV account. Series match exactly by TMDB id (the series list carries it); movies match by normalized
/// title + year (the bulk VOD list has no TMDB id), with an optional per-stream TMDB confirmation. The big indexes are
/// built once and cached; a concurrent burst shares one build (<see cref="SingleFlight"/>).
/// </summary>
public sealed class XtreamCatalog
{
    private const string SeriesKey = "series-index";
    private const string MovieKey = "movie-index";

    private readonly XtreamClient _client;
    private readonly SingleFlight _flights = new();
    private readonly TtlCache<IReadOnlyDictionary<int, int>> _seriesIndex;
    private readonly TtlCache<XtreamMovieIndex> _movieIndex;
    private readonly TtlCache<XtreamSeriesInfo> _seriesInfo;

    public XtreamCatalog(XtreamClient client, TimeProvider clock)
    {
        _client = client;
        _seriesIndex = new TtlCache<IReadOnlyDictionary<int, int>>(clock, TimeSpan.FromHours(6), capacity: 1);
        _movieIndex = new TtlCache<XtreamMovieIndex>(clock, TimeSpan.FromHours(6), capacity: 1);
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
        var index = await SeriesIndexAsync(cancellationToken).ConfigureAwait(false);
        return index.TryGetValue(tmdbId, out var seriesId) ? seriesId : null;
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
        return MatchMovie(index, title, year);
    }

    /// <summary>The <see cref="FindMovieAsync"/> rule, over an already-built index (the testable core).</summary>
    internal static XtreamMovieMatch? MatchMovie(XtreamMovieIndex index, string title, int? year)
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

        // No year, or no exact title+year pair: accept a title that names exactly one movie on the source.
        return index.ByTitle.TryGetValue(normalized, out var sameTitle) && sameTitle.Count == 1 ? sameTitle[0] : null;
    }

    /// <summary>Builds the <c>tmdb_id -&gt; series_id</c> map from the bulk series list (the testable core).</summary>
    internal static IReadOnlyDictionary<int, int> BuildSeriesIndex(IReadOnlyList<XtreamSeriesItem> series)
    {
        var map = new Dictionary<int, int>(series.Count);
        foreach (var s in series)
        {
            // Keep the first id for a TMDB id; later duplicates (re-uploads) are ignored.
            if (s.TmdbId is { } tmdb && tmdb > 0)
            {
                map.TryAdd(tmdb, s.SeriesId);
            }
        }

        return map;
    }

    /// <summary>Builds the movie lookup tables from the bulk VOD list (the testable core).</summary>
    internal static XtreamMovieIndex BuildMovieIndex(IReadOnlyList<XtreamVodItem> movies)
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

        return new XtreamMovieIndex(byTitleYear, byTitle.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<XtreamMovieMatch>)kv.Value));
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

    private Task<IReadOnlyDictionary<int, int>> SeriesIndexAsync(CancellationToken cancellationToken)
    {
        if (_seriesIndex.TryGet(SeriesKey, out var cached))
        {
            return Task.FromResult(cached);
        }

        return _flights.RunAsync(SeriesKey, async () =>
        {
            var series = await _client.GetSeriesAsync(cancellationToken).ConfigureAwait(false);
            var result = BuildSeriesIndex(series);
            _seriesIndex.Set(SeriesKey, result);
            return result;
        }, cancellationToken);
    }

    private Task<XtreamMovieIndex> MovieIndexAsync(CancellationToken cancellationToken)
    {
        if (_movieIndex.TryGet(MovieKey, out var cached))
        {
            return Task.FromResult(cached);
        }

        return _flights.RunAsync(MovieKey, async () =>
        {
            var movies = await _client.GetVodStreamsAsync(cancellationToken).ConfigureAwait(false);
            var result = BuildMovieIndex(movies);
            _movieIndex.Set(MovieKey, result);
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
