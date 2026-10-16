using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.PortalitoTV.Configuration;
using Jellyfin.Plugin.PortalitoTV.Content;
using Jellyfin.Plugin.PortalitoTV.Media;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using Jellyfin.Plugin.PortalitoTV.Util;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Api;

/// <summary>The changes to one series in a "Guardar y aplicar" batch.</summary>
public sealed class SeriesChange
{
    /// <summary>Gets or sets the series key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the series is used in channels.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the unchecked seasons.</summary>
    public List<int> ExcludedSeasons { get; set; } = new();

    /// <summary>Gets or sets the audio/subtitle choice.</summary>
    public EmitLanguage Language { get; set; }

    /// <summary>Gets or sets the Portalito seasons assigned by hand to a TMDB season.</summary>
    public List<SeasonMapEntry> SeasonMap { get; set; } = new();
}

/// <summary>The changes to one movie in a "Guardar y aplicar" batch.</summary>
public sealed class MovieChange
{
    /// <summary>Gets or sets the movie's Jellyfin id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the audio/subtitle choice.</summary>
    public EmitLanguage Language { get; set; }
}

/// <summary>Everything "Guardar y aplicar" sends.</summary>
public sealed class ApplyRequest
{
    /// <summary>Gets or sets the series changes.</summary>
    public List<SeriesChange> Series { get; set; } = new();

    /// <summary>Gets or sets the movie changes.</summary>
    public List<MovieChange> Movies { get; set; } = new();

    /// <summary>Gets or sets the channels (the full list: the ones missing are deleted).</summary>
    public List<ChannelSettings> Channels { get; set; } = new();

    /// <summary>Gets or sets the time zone (null = don't change; "" = the server's).</summary>
    public string? TimeZone { get; set; }

    /// <summary>Gets or sets the favorites user (null = don't change; "" = the first administrator).</summary>
    public string? FavoritesUser { get; set; }

    /// <summary>Gets or sets the address of this Jellyfin as ffmpeg reaches it (null = don't change; "" = the default).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Gets or sets the VAAPI render device (null = don't change; "" = the default).</summary>
    public string? VaapiDevice { get; set; }

    /// <summary>Gets or sets the TMDB token (null = don't change; "" = delete).</summary>
    public string? TmdbToken { get; set; }
}

/// <summary>A program of the preview.</summary>
/// <param name="StartUtc">Start.</param>
/// <param name="EndUtc">End.</param>
/// <param name="Title">Series or movie title.</param>
/// <param name="EpisodeTitle">Episode title.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="IsFiller">Whether it is filler.</param>
/// <param name="IsSlate">Whether it is the black slate.</param>
/// <param name="IsMovie">Whether it is a movie.</param>
public sealed record PreviewItem(DateTime StartUtc, DateTime EndUtc, string Title, string? EpisodeTitle, int? Season, int? Episode,
    bool IsFiller, bool IsSlate, bool IsMovie);

/// <summary>The configuration page's API (administrators only).</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("PortalitoTV/admin")]
[Produces("application/json")]
public sealed partial class ConfigApiController : ControllerBase
{
    private static readonly SemaphoreSlim ApplyLock = new(1, 1);

    private readonly ContentCatalog _catalog;
    private readonly PlayoutManager _playouts;
    private readonly ChannelService _service;
    private readonly ITaskManager _tasks;
    private readonly ILibraryManager _library;
    private readonly AudioSampler _sampler;
    private readonly ILogger<ConfigApiController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConfigApiController"/> class.</summary>
    /// <param name="catalog">The content catalog.</param>
    /// <param name="playouts">The schedules.</param>
    /// <param name="service">The channel service.</param>
    /// <param name="tasks">Jellyfin's task manager.</param>
    /// <param name="library">The library.</param>
    /// <param name="sampler">The audio sampler.</param>
    /// <param name="logger">Logger.</param>
    public ConfigApiController(ContentCatalog catalog, PlayoutManager playouts, ChannelService service, ITaskManager tasks,
        ILibraryManager library, AudioSampler sampler, ILogger<ConfigApiController> logger)
    {
        _library = library;
        _sampler = sampler;
        _catalog = catalog;
        _playouts = playouts;
        _service = service;
        _tasks = tasks;
        _logger = logger;
    }

    /// <summary>The page's state: hearted series and movies, channels and settings.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    [HttpGet("state")]
    public async Task<ActionResult> State(CancellationToken ct)
    {
        if (_catalog.Series.Count == 0)
        {
            await _catalog.RefreshAsync(ct).ConfigureAwait(false);
        }

        var config = Plugin.Instance!.Configuration;
        return Ok(new
        {
            Series = _catalog.Series,
            Movies = _catalog.Movies,
            config.Channels,
            config.TimeZone,
            config.FavoritesUser,
            ServerUrl = ServerUrl.Normalize(config.ServerUrl),
            config.VaapiDevice,
            HasTmdbToken = !string.IsNullOrEmpty(config.TmdbToken),
            Pending = _catalog.PendingCount,
        });
    }

    /// <summary>Progress of the sync with Portalito (the page polls it every second and a half while it runs).</summary>
    /// <returns>The state of the last sync.</returns>
    [HttpGet("sync/status")]
    public ActionResult SyncStatusGet() => Ok(SyncTracker.Snapshot());

    /// <summary>
    /// "Sincronizar con Portalito": runs the Siguiendo sync and then re-reads the hearts (series and movies), probes what is new
    /// and rebuilds the schedule. Progress is read from <see cref="SyncStatusGet"/>.
    /// </summary>
    /// <returns>202 with the sync's number.</returns>
    [HttpPost("sync")]
    public ActionResult Sync()
    {
        var sync = _tasks.ScheduledTasks.FirstOrDefault(t =>
            t.ScheduledTask.Name.Contains("Siguiendo", StringComparison.OrdinalIgnoreCase)
            && t.ScheduledTask.Name.Contains("Portalito", StringComparison.OrdinalIgnoreCase));
        if (!SyncTracker.TryStart("Iniciando…", out var status))
        {
            return Accepted(new { Started = true, AlreadyRunning = true, status.RunId });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Stage 1 (0-55 %): Jellyfin's "Sincronizar Portalito · Siguiendo" task (if it was already running it is waited for
                // and another is started; see SyncTaskRunner).
                if (sync is not null)
                {
                    await SyncTaskRunner.RunAsync(
                        () => sync.State,
                        () => sync.CurrentProgress,
                        () => _tasks.Execute(sync, new TaskOptions()),
                        (percent, text) => SyncTracker.Update(percent, text),
                        "Esperando la sincronización de Siguiendo que ya estaba en curso…",
                        "Sincronizando Siguiendo con Portalito…",
                        SyncTaskRunner.Timing.Default).ConfigureAwait(false);
                }

                // Stage 2 (55-100 %): read the hearts, probe new episodes and rebuild the schedule.
                SyncTracker.Update(55, "Leyendo tus ♥ de Portalito…");
                await _service.RunAsync(
                    TimeSpan.FromMinutes(5),
                    reprogram: false,
                    new InlineProgress(p => SyncTracker.Update(55 + (45 * p / 100))),
                    CancellationToken.None,
                    phase: text => SyncTracker.Update(0, text)).ConfigureAwait(false);
                SyncTracker.Finish(null, _catalog.PendingCount);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Sync with Portalito failed");
                SyncTracker.Finish("No se pudo completar la sincronización. Revisa el registro de Jellyfin.", _catalog.PendingCount);
            }
        });
        return Accepted(new { Started = sync is not null, status.RunId });
    }

    /// <summary>"Escuchar": 15 s of an audio track (to tell Latin American from Castilian Spanish by ear).</summary>
    /// <param name="itemId">The episode or movie.</param>
    /// <param name="track">Index among the audio tracks.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes when the sample has been sent.</returns>
    [HttpGet("sample")]
    public async Task Sample([FromQuery] Guid itemId, [FromQuery] int track, CancellationToken ct)
    {
        var item = _library.GetItemById(itemId);
        if (item is null || track < 0)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // It is built in memory before answering: if the portal fails an error is returned, not a cut MP3.
        using var mp3 = new MemoryStream();
        if (!await _sampler.WriteSampleAsync(item, track, mp3, ct).ConfigureAwait(false) || mp3.Length == 0)
        {
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        Response.ContentType = "audio/mpeg";
        mp3.Position = 0;
        await mp3.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
    }

    /// <summary>What a channel's next 24 hours would look like with the settings on the page (nothing is saved).</summary>
    /// <param name="draft">The channel settings to try.</param>
    /// <returns>The programs.</returns>
    [HttpPost("preview")]
    public ActionResult<IEnumerable<PreviewItem>> Preview([FromBody] ChannelSettings draft)
    {
        var items = _playouts.Preview(draft, TimeSpan.FromHours(24));
        return Ok(items.Select(i => new PreviewItem(i.StartUtc, i.EndUtc, i.Title, i.EpisodeTitle, i.Season, i.Episode,
            i.IsFiller, i.IsSlate, i.IsMovie)));
    }

    /// <summary>"Guardar y aplicar": saves the page's settings and rebuilds the schedule from the program on air.</summary>
    /// <param name="request">Everything the page sends.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the episodes still to probe, or 400 with the reason.</returns>
    [HttpPost("apply")]
    public async Task<ActionResult> Apply([FromBody] ApplyRequest request, CancellationToken ct)
    {
        var error = Validate(request);
        if (error is not null)
        {
            return BadRequest(new { Error = error });
        }

        await ApplyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance!;
            var config = plugin.Configuration;
            foreach (var change in request.Series)
            {
                var s = config.Series.FirstOrDefault(x => x.PortalitoId == change.Key);
                if (s is not null)
                {
                    s.Enabled = change.Enabled;
                    s.ExcludedSeasons = change.ExcludedSeasons.Distinct().OrderBy(n => n).ToList();
                    s.Language = change.Language;
                    s.SeasonMap = change.SeasonMap.Where(m => m.Season > 0 && m.TmdbSeason > 0)
                        .GroupBy(m => m.Season).Select(g => g.Last()).OrderBy(m => m.Season).ToList();
                }
            }

            foreach (var change in request.Movies)
            {
                var id = change.ItemId.ToString("N");
                var m = config.Movies.FirstOrDefault(x => x.ItemId == id);
                if (m is null)
                {
                    m = new MovieSettings { ItemId = id, Name = _catalog.Movies.FirstOrDefault(x => x.ItemId == change.ItemId)?.Name ?? string.Empty };
                    config.Movies.Add(m);
                }

                m.Language = change.Language;
            }

            var removed = config.Channels.Select(c => c.Number).Except(request.Channels.Select(c => c.Number)).ToList();
            config.Channels = request.Channels;
            if (request.TimeZone is not null)
            {
                config.TimeZone = request.TimeZone.Trim();
            }

            if (request.FavoritesUser is not null)
            {
                config.FavoritesUser = request.FavoritesUser.Trim();
            }

            if (request.ServerUrl is not null)
            {
                config.ServerUrl = request.ServerUrl.Trim().Length == 0 ? ServerUrl.Default : request.ServerUrl.Trim().TrimEnd('/');
            }

            if (request.VaapiDevice is not null)
            {
                config.VaapiDevice = request.VaapiDevice.Trim();
            }

            if (request.TmdbToken is not null)
            {
                config.TmdbToken = request.TmdbToken.Trim();
            }

            plugin.SaveConfiguration();
            foreach (var number in removed)
            {
                _playouts.Forget(number);
            }

            // Rebuild from the program on air and refresh Jellyfin's guide.
            await _service.RunAsync(TimeSpan.FromMinutes(1), reprogram: true, null, ct).ConfigureAwait(false);
            _logger.LogInformation("Configuration applied: {Channels} channels", config.Channels.Count);
            return Ok(new { Pending = _catalog.PendingCount });
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimeFormat();

    // The messages are shown to the user on the page, so they are in Spanish.
    internal static string? Validate(ApplyRequest request)
    {
        var numbers = new HashSet<string>();
        foreach (var ch in request.Channels)
        {
            ch.Number = (ch.Number ?? string.Empty).Trim();
            ch.Name = (ch.Name ?? string.Empty).Trim();
            if (!int.TryParse(ch.Number, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return $"El número de canal \"{ch.Number}\" no es válido.";
            }

            if (!numbers.Add(ch.Number))
            {
                return $"El canal {ch.Number} está repetido.";
            }

            if (ch.Name.Length == 0)
            {
                return $"El canal {ch.Number} no tiene nombre.";
            }

            foreach (var b in ch.Blocks)
            {
                if (!TimeFormat().IsMatch(b.Start ?? string.Empty))
                {
                    return $"Canal {ch.Number}: la hora \"{b.Start}\" no es válida (HH:mm).";
                }

                b.End = string.IsNullOrWhiteSpace(b.End) ? null : b.End.Trim();
                if (b.End is not null && !TimeFormat().IsMatch(b.End))
                {
                    return $"Canal {ch.Number}: la hora de fin \"{b.End}\" no es válida (HH:mm).";
                }

                if (b.End == b.Start)
                {
                    return $"Canal {ch.Number}: el bloque de las {b.Start} empieza y termina a la misma hora.";
                }
            }

            if (ch.Mode == ChannelMode.Schedule && ch.Blocks.GroupBy(b => (b.Start, string.Join(',', b.Days.OrderBy(d => d)))).Any(g => g.Count() > 1))
            {
                return $"Canal {ch.Number}: hay dos bloques a la misma hora y los mismos días.";
            }
        }

        if (request.TimeZone is { Length: > 0 } tz && !string.IsNullOrWhiteSpace(tz))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(tz.Trim());
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return $"La zona horaria \"{tz}\" no existe.";
            }
        }

        if (request.ServerUrl is { } url && url.Trim().Length > 0
            && !(Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0))
        {
            return $"La dirección del servidor \"{url}\" no es válida (por ejemplo http://127.0.0.1:8096).";
        }

        return null;
    }

    /// <summary>A synchronous <see cref="IProgress{T}"/> (<see cref="Progress{T}"/> delivers its reports on another thread and out of order).</summary>
    private sealed class InlineProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public InlineProgress(Action<double> report) => _report = report;

        public void Report(double value) => _report(value);
    }
}
