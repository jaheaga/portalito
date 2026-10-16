using System.Collections.Concurrent;
using Jellyfin.Plugin.PortalitoTV.Configuration;
using Jellyfin.Plugin.PortalitoTV.Media;
using Jellyfin.Plugin.PortalitoTV.Metadata;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Content;

/// <summary>Summary of a hearted series for the configuration page.</summary>
/// <param name="Key">The series key.</param>
/// <param name="Name">Display name.</param>
/// <param name="PortalitoId">Portalito id (null if it isn't in Siguiendo yet).</param>
/// <param name="JellyfinId">The series' Jellyfin id.</param>
/// <param name="InSiguiendo">Whether it is already in the Siguiendo library.</param>
/// <param name="Enabled">Whether it is used in channels.</param>
/// <param name="IsNew">Whether it appeared recently.</param>
/// <param name="Language">The language the user chose.</param>
/// <param name="DetectedLanguage">The language Auto would pick.</param>
/// <param name="Seasons">The seasons.</param>
/// <param name="ReadyEpisodes">Episodes with a known duration.</param>
/// <param name="PendingEpisodes">Episodes still to probe.</param>
/// <param name="AverageMinutes">Average episode length.</param>
/// <param name="MaxMinutes">Longest episode.</param>
/// <param name="SampleItemId">An episode to listen to samples from.</param>
/// <param name="SampleAudio">That episode's audio tracks.</param>
/// <param name="TmdbOrdering">The TMDB episode group chosen to match Portalito's numbering (null = TMDB's default).</param>
public sealed record SeriesSummary(
    string Key,
    string Name,
    string? PortalitoId,
    Guid? JellyfinId,
    bool InSiguiendo,
    bool Enabled,
    bool IsNew,
    EmitLanguage Language,
    EmitLanguage DetectedLanguage,
    IReadOnlyList<SeasonSummary> Seasons,
    int ReadyEpisodes,
    int PendingEpisodes,
    double AverageMinutes,
    double MaxMinutes,
    Guid? SampleItemId,
    IReadOnlyList<AudioTrack>? SampleAudio,
    string? TmdbOrdering = null);

/// <summary>A season of a hearted series.</summary>
/// <param name="Number">Season number in Portalito.</param>
/// <param name="Episodes">Episodes in Portalito.</param>
/// <param name="Selected">Whether the season is used in channels.</param>
/// <param name="TmdbEpisodes">Episodes of the season in the chosen TMDB numbering (null if there is no TMDB or it doesn't have it).</param>
/// <param name="Matches">Matches Portalito (same episode count): TMDB data is only used then.</param>
/// <param name="MappedTo">TMDB season assigned by hand to this season (null = none).</param>
/// <param name="Options">TMDB seasons suggested to assign (only for the ones that don't match): the same season and those with the same episode count.</param>
public sealed record SeasonSummary(int Number, int Episodes, bool Selected, int? TmdbEpisodes = null, bool Matches = false,
    int? MappedTo = null, IReadOnlyList<SeasonOption>? Options = null);

/// <summary>A TMDB season offered for a manual assignment.</summary>
/// <param name="Season">TMDB season number.</param>
/// <param name="Episodes">Its episode count.</param>
public sealed record SeasonOption(int Season, int Episodes);

/// <summary>A hearted movie for the configuration page.</summary>
/// <param name="ItemId">The Jellyfin id.</param>
/// <param name="Name">Title.</param>
/// <param name="Year">Production year.</param>
/// <param name="Minutes">Length in minutes (0 = not probed yet).</param>
/// <param name="Language">The language the user chose.</param>
/// <param name="Audio">Its audio tracks.</param>
/// <param name="Ready">Whether its duration is known.</param>
public sealed record MovieSummary(Guid ItemId, string Name, int? Year, double Minutes, EmitLanguage Language,
    IReadOnlyList<AudioTrack>? Audio, bool Ready);

/// <summary>
/// Schedulable content: episodes (of the active hearted series and checked seasons) and hearted movies, with a known duration
/// and guide data. Only what has a duration gets in; the rest is probed in the background.
/// </summary>
public sealed class ContentCatalog : IContentLookup
{
    private readonly PortalitoLibrary _library;
    private readonly ProbeCache _probe;
    private readonly TmdbClient _tmdb;
    private readonly InputResolver _inputs;
    private readonly ILogger<ContentCatalog> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IReadOnlyList<ContentEntry>> _mixes = new();

    private Dictionary<string, IReadOnlyList<ContentEntry>> _collections = new();
    private List<SeriesSummary> _summaries = new();
    private List<MovieSummary> _movieSummaries = new();
    private List<BaseItem> _pending = new();
    private List<BaseItem> _samples = new();

    /// <summary>Initializes a new instance of the <see cref="ContentCatalog"/> class.</summary>
    /// <param name="library">The Portalito library reader.</param>
    /// <param name="probe">Probed durations and tracks.</param>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="inputs">Input URL resolver.</param>
    /// <param name="logger">Logger.</param>
    public ContentCatalog(PortalitoLibrary library, ProbeCache probe, TmdbClient tmdb, InputResolver inputs, ILogger<ContentCatalog> logger)
    {
        _library = library;
        _probe = probe;
        _tmdb = tmdb;
        _inputs = inputs;
        _logger = logger;
    }

    /// <summary>Gets the hearted series.</summary>
    public IReadOnlyList<SeriesSummary> Series => _summaries;

    /// <summary>Gets the hearted movies.</summary>
    public IReadOnlyList<MovieSummary> Movies => _movieSummaries;

    /// <summary>Gets how many items still have to be probed.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Gets how many sample items still need their audio tracks measured (to pick a language on the page).</summary>
    public int SampleCount => _samples.Count(i => _probe.ShouldProbe(InputResolver.ProbeKey(i), needAudio: true));

    /// <summary>The TMDB seasons suggested for a Portalito season that doesn't match: the same one, those with the same episode count and the one already assigned.</summary>
    /// <param name="tmdbCounts">TMDB season → episode count.</param>
    /// <param name="season">The Portalito season.</param>
    /// <param name="count">Its episode count.</param>
    /// <param name="span">Its highest episode number.</param>
    /// <param name="mapped">The TMDB season already assigned, if any.</param>
    /// <returns>The suggestions.</returns>
    public static IReadOnlyList<SeasonOption> SeasonOptions(IReadOnlyDictionary<int, int> tmdbCounts, int season, int count, int span, int? mapped)
    {
        var picks = new List<int>();
        if (tmdbCounts.ContainsKey(season))
        {
            picks.Add(season);
        }

        if (mapped is { } m && tmdbCounts.ContainsKey(m) && !picks.Contains(m))
        {
            picks.Add(m);
        }

        picks.AddRange(tmdbCounts.Where(kv => (kv.Value == count || kv.Value == span) && !picks.Contains(kv.Key))
            .OrderBy(kv => Math.Abs(kv.Key - season)).Select(kv => kv.Key).Take(8));
        return picks.Select(s => new SeasonOption(s, tmdbCounts[s])).ToList();
    }

    /// <summary>The current guide data of an item (to refresh what is already scheduled).</summary>
    /// <param name="itemId">The Jellyfin id.</param>
    /// <returns>The data, or null if the item is gone.</returns>
    public PlayoutItem? Template(Guid itemId)
    {
        foreach (var list in _collections.Values)
        {
            foreach (var e in list)
            {
                if (e.ItemId == itemId)
                {
                    return e.Template;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentEntry> Collection(string key)
    {
        if (key.StartsWith("mix:", StringComparison.Ordinal))
        {
            return _mixes.GetOrAdd(key, k => k[4..].Split('|', StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(Collection).DistinctBy(e => e.ItemId).ToList());
        }

        return _collections.TryGetValue(key, out var list) ? list : Array.Empty<ContentEntry>();
    }

    /// <summary>Reads Jellyfin again (hearts, episodes, movies) and rebuilds the collections.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes when the collections are rebuilt.</returns>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Probes (ffprobe) what is missing, in parallel, until the given time runs out.</summary>
    /// <param name="budget">The most time to spend.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="onProgress">Called after each processed item with (processed, total) to show progress.</param>
    /// <returns>How many items were probed.</returns>
    public async Task<int> ProbePendingAsync(TimeSpan budget, CancellationToken ct, Action<int, int>? onProgress = null)
    {
        var deadline = DateTime.UtcNow + budget;
        var queue = new ConcurrentQueue<BaseItem>(
            _samples.Where(i => _probe.ShouldProbe(InputResolver.ProbeKey(i), needAudio: true))
                .Concat(_pending.Where(i => _probe.ShouldProbe(InputResolver.ProbeKey(i), needAudio: false))));
        int done = 0, processed = 0, total = queue.Count;
        var workers = Enumerable.Range(0, 3).Select(async _ =>
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && queue.TryDequeue(out var item))
            {
                var url = await _inputs.ResolveAsync(item, ct).ConfigureAwait(false);
                if (url is not null && await _probe.ProbeAsync(InputResolver.ProbeKey(item), url, ct).ConfigureAwait(false) is not null)
                {
                    Interlocked.Increment(ref done);
                    if (done % 25 == 0)
                    {
                        _probe.SaveIfDirty();
                    }
                }

                onProgress?.Invoke(Interlocked.Increment(ref processed), total);
            }
        });
        await Task.WhenAll(workers).ConfigureAwait(false);
        _probe.SaveIfDirty();
        return done;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !TmdbClient.IsGenericTitle(v));

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var plugin = Plugin.Instance!;
        var config = plugin.Configuration;
        var collections = new Dictionary<string, IReadOnlyList<ContentEntry>>();
        var summaries = new List<SeriesSummary>();
        var pending = new List<BaseItem>();
        var samples = new List<BaseItem>();
        bool configChanged = false;

        foreach (var fav in _library.FavoriteSeriesList())
        {
            var settings = config.Series.FirstOrDefault(s => s.PortalitoId == fav.Key);
            if (settings is null)
            {
                settings = new SeriesSettings { PortalitoId = fav.Key, Name = fav.Name };
                config.Series.Add(settings);
                configChanged = true;
            }
            else if (settings.Name != fav.Name)
            {
                settings.Name = fav.Name;
                configChanged = true;
            }

            var entries = new List<ContentEntry>();
            var seasons = new List<SeasonSummary>();
            int pendingEps = 0;
            var detected = EmitLanguage.Auto;
            Guid? sampleId = null;
            IReadOnlyList<AudioTrack>? sampleAudio = null;
            string? tmdbOrdering = null;
            var lastEpisode = new Dictionary<int, int>();
            if (fav.Library is not null)
            {
                var episodes = _library.Episodes(fav.Library);
                lastEpisode = episodes.GroupBy(e => e.Season).ToDictionary(g => g.Key, g => g.Max(e => e.Episode));
                var folder = Path.GetFileName(fav.Library.Path ?? string.Empty).Split(" [")[0];
                var show = await _tmdb.ShowAsync(folder, ct).ConfigureAwait(false);

                // Portalito doesn't always number like TMDB (some shows split or merge seasons): the numbering that matches is
                // chosen, and seasons that don't match show no data from another episode.
                var ordering = show is null ? null : await _tmdb.OrderingAsync(
                    show.Id, episodes.GroupBy(e => e.Season).ToDictionary(g => g.Key, g => g.Count()), ct,
                    episodes.GroupBy(e => e.Season).ToDictionary(g => g.Key, g => g.Max(e => e.Episode))).ConfigureAwait(false);
                tmdbOrdering = ordering?.GroupName;
                foreach (var g in episodes.GroupBy(e => e.Season))
                {
                    int? tmdbCount = ordering?.Counts is { } counts && counts.TryGetValue(g.Key, out var n) ? n : null;
                    bool trusted = ordering?.TrustedSeasons.Contains(g.Key) == true;
                    int? mappedTo = settings.SeasonMap.FirstOrDefault(m => m.Season == g.Key)?.TmdbSeason;
                    seasons.Add(new SeasonSummary(g.Key, g.Count(), !settings.ExcludedSeasons.Contains(g.Key), tmdbCount, trusted, mappedTo,
                        trusted || ordering?.Counts is null ? null : SeasonOptions(ordering.Counts, g.Key, g.Count(), g.Max(e => e.Episode), mappedTo)));
                }

                if (ordering is not null && ordering.TrustedSeasons.Count < seasons.Count)
                {
                    _logger.LogInformation("{Series}: TMDB numbering \"{Ordering}\", no per-episode data in seasons {Seasons}",
                        fav.Name, ordering.GroupName, string.Join(',', seasons.Where(s => !s.Matches).Select(s =>
                            $"{s.Number} ({s.Episodes} vs {(s.TmdbEpisodes?.ToString() ?? "-")}{(s.MappedTo is { } m ? $", assigned to S{m}" : string.Empty)})")));
                }

                foreach (var ep in episodes)
                {
                    var probe = ProbeOf(ep.RelPath, ep.Item);
                    if (detected == EmitLanguage.Auto && probe?.Audio is { Count: > 0 } audio)
                    {
                        detected = TrackSelector.Detect(audio);
                        sampleId = ep.Item.Id; // sample episode for "Escuchar" on the page
                        sampleAudio = audio;
                    }

                    if (settings.ExcludedSeasons.Contains(ep.Season))
                    {
                        continue;
                    }

                    if (probe is null || probe.DurationMs <= 0)
                    {
                        pendingEps++;
                        if (settings.Enabled)
                        {
                            pending.Add(ep.Item);
                        }

                        continue;
                    }

                    // A hand-assigned season: that TMDB season's data is requested, by episode number, even if the count doesn't match.
                    var map = settings.SeasonMap.FirstOrDefault(m => m.Season == ep.Season);
                    var metaOrdering = map is null || ordering is null || ordering.TrustedSeasons.Contains(map.TmdbSeason)
                        ? ordering
                        : ordering with { TrustedSeasons = new HashSet<int>(ordering.TrustedSeasons) { map.TmdbSeason } };
                    var meta = show is null ? null : await _tmdb.EpisodeAsync(show.Id, metaOrdering, map?.TmdbSeason ?? ep.Season, ep.Episode, ct).ConfigureAwait(false);
                    var duration = TimeSpan.FromSeconds(Math.Floor(probe.Duration.TotalSeconds));
                    var template = new PlayoutItem(
                        ep.Item.Id,
                        DateTime.MinValue,
                        DateTime.MinValue + duration,
                        TimeSpan.Zero,
                        settings.Language,
                        PortalitoLibrary.DisplayName(fav.Name),
                        EpisodeTitle: FirstNonEmpty(meta?.Name, ep.Item.Name),
                        Season: ep.Season,
                        Episode: ep.Episode,
                        Overview: FirstNonEmpty(meta?.Overview, ep.Item.Overview, ordering?.SeriesOverview),
                        ImageUrl: TmdbClient.Image(meta?.Still, "w780") ?? TmdbClient.Image(show?.Backdrop, "w780")
                                  ?? TmdbClient.Image(show?.Poster, "w500"));
                    entries.Add(new ContentEntry(ep.Item.Id, duration, ep.Season, ep.Episode, template));
                }
            }

            if (detected == EmitLanguage.Auto && fav.Library is not null && _library.Episodes(fav.Library).FirstOrDefault() is { } sample)
            {
                samples.Add(sample.Item); // probe its tracks to show the language on the page
            }

            if (settings.Enabled && entries.Count > 0)
            {
                // Multi-part episodes ("primera parte", "(1)"...) are marked so they are scheduled together and in order.
                var grouped = MultiPart.Assign(entries, lastEpisode);
                collections[PlayoutBuilder.SeriesKey(fav.Key)] = grouped;
                var firstParts = grouped.Where(e => e.Template.PartIndex == 1).ToList();
                if (firstParts.Count > 0)
                {
                    _logger.LogInformation("{Series}: {Count} multi-part episodes are scheduled together: {Parts}", fav.Name, firstParts.Count,
                        string.Join("; ", firstParts.Select(e => $"S{e.Season}E{e.Episode} x{e.Template.PartCount} {e.Template.EpisodeTitle}")));
                }
            }

            summaries.Add(new SeriesSummary(
                fav.Key,
                PortalitoLibrary.DisplayName(fav.Name),
                fav.PortalitoId,
                fav.Library?.Id,
                fav.Library is not null,
                settings.Enabled,
                DateTime.UtcNow - settings.FirstSeenUtc < TimeSpan.FromDays(3),
                settings.Language,
                detected,
                seasons,
                entries.Count,
                pendingEps,
                entries.Count == 0 ? 0 : Math.Round(entries.Average(e => e.Duration.TotalMinutes), 1),
                entries.Count == 0 ? 0 : Math.Round(entries.Max(e => e.Duration.TotalMinutes), 1),
                sampleId,
                sampleAudio,
                tmdbOrdering));
        }

        // Hearted movies.
        var movies = new List<ContentEntry>();
        var movieSummaries = new List<MovieSummary>();
        foreach (var m in _library.FavoriteMovies())
        {
            var probe = _probe.Get(InputResolver.ProbeKey(m));
            var language = config.Movies.FirstOrDefault(x => x.ItemId == m.Id.ToString("N"))?.Language ?? EmitLanguage.Auto;
            if (probe?.Audio is null)
            {
                samples.Add(m); // its tracks, to pick a language
            }

            movieSummaries.Add(new MovieSummary(m.Id, m.Name, m.ProductionYear,
                probe is null ? 0 : Math.Round(probe.Duration.TotalMinutes), language, probe?.Audio, probe is { DurationMs: > 0 }));
            if (probe is null || probe.DurationMs <= 0)
            {
                pending.Add(m);
                continue;
            }

            var meta = await _tmdb.MovieAsync(m.Name, m.ProductionYear, ct).ConfigureAwait(false);
            var duration = TimeSpan.FromSeconds(Math.Floor(probe.Duration.TotalSeconds));
            var template = new PlayoutItem(
                m.Id,
                DateTime.MinValue,
                DateTime.MinValue + duration,
                TimeSpan.Zero,
                language,
                m.Name,
                Overview: FirstNonEmpty(m.Overview, meta?.Overview),
                ImageUrl: meta?.Image,
                Year: m.ProductionYear,
                IsMovie: true);
            var movieEntry = new ContentEntry(m.Id, duration, 0, 0, template);
            movies.Add(movieEntry);
            collections[PlayoutBuilder.MovieKey(m.Id)] = new List<ContentEntry> { movieEntry }; // a block with that movie
        }

        if (movies.Count > 0)
        {
            collections[PlayoutBuilder.MoviesKey] = movies;
        }

        _collections = collections;
        _mixes.Clear();
        _summaries = summaries;
        _movieSummaries = movieSummaries;
        _pending = pending;
        _samples = samples;
        _tmdb.SaveIfDirty();
        _probe.SaveIfDirty();
        if (configChanged)
        {
            plugin.SaveConfiguration();
        }

        _logger.LogInformation("Catalog: {Series} series, {Episodes} episodes ready, {Movies} movies, {Pending} to probe",
            summaries.Count,
            collections.Where(c => c.Key.StartsWith("series:", StringComparison.Ordinal)).Sum(c => c.Value.Count),
            movies.Count,
            pending.Count);
    }

    /// <summary>What was measured for an episode: the cache, or what Jellyfin already knows (some files report everything).</summary>
    private ProbeEntry? ProbeOf(string relPath, BaseItem item)
    {
        var cached = _probe.Get(relPath);
        if (cached is { DurationMs: > 0, Audio: not null })
        {
            return cached;
        }

        if (item.RunTimeTicks is > 0)
        {
            var audio = item.GetMediaStreams()
                .Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal)
                .OrderBy(s => s.Index)
                .Select(s => new AudioTrack(s.Language, s.Title))
                .ToList();
            _probe.SetDurationIfMissing(relPath, TimeSpan.FromTicks(item.RunTimeTicks.Value).Ticks / TimeSpan.TicksPerMillisecond,
                audio.Count > 0 ? audio : null);
            return _probe.Get(relPath);
        }

        return cached;
    }
}
