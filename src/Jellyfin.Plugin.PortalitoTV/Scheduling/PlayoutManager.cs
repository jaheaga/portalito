using System.Text.Json;
using Jellyfin.Plugin.PortalitoTV.Configuration;
using Jellyfin.Plugin.PortalitoTV.Content;
using Jellyfin.Plugin.PortalitoTV.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Scheduling;

/// <summary>The grids of every channel: stores them, extends them and serves them to the stream and the guide.</summary>
public sealed class PlayoutManager : IPlayoutSource
{
    /// <summary>How far ahead the grids are built.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(48);

    private static readonly TimeSpan KeepPast = TimeSpan.FromHours(6);

    private readonly ContentCatalog _catalog;
    private readonly ILogger<PlayoutManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, IReadOnlyList<PlayoutItem>> _snapshots = new();

    /// <summary>Initializes a new instance of the <see cref="PlayoutManager"/> class.</summary>
    /// <param name="catalog">The content catalog.</param>
    /// <param name="logger">Logger.</param>
    public PlayoutManager(ContentCatalog catalog, ILogger<PlayoutManager> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>Gets the time zone block start times are read in: the configured one, else the server's.</summary>
    public static TimeZoneInfo TimeZone => ResolveTimeZone(Plugin.Instance!.Configuration.TimeZone);

    /// <inheritdoc />
    public IReadOnlyList<ChannelDef> Channels =>
        Plugin.Instance!.Configuration.Channels
            .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Number))
            .Select(c => new ChannelDef(c.Number, c.Name, c.LogoUrl))
            .ToList();

    private static string Dir => Path.Combine(Plugin.Instance!.WorkDir, "playouts");

    /// <summary>Resolves the configured time zone.</summary>
    /// <param name="id">The configured id (empty = the server's time zone).</param>
    /// <returns>The zone; UTC if the id is unknown.</returns>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <inheritdoc />
    public IEnumerable<PlayoutItem> Between(string channel, DateTime fromUtc, DateTime toUtc) =>
        Snapshot(channel).Where(i => i.EndUtc > fromUtc && i.StartUtc < toUtc);

    /// <inheritdoc />
    public PlayoutItem? At(string channel, DateTime utc) =>
        Snapshot(channel).FirstOrDefault(i => i.StartUtc <= utc && utc < i.EndUtc);

    /// <summary>Extends every grid up to the horizon.</summary>
    /// <param name="reprogram">After a configuration change: drops what comes after the program on air.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes when the grids are saved.</returns>
    public async Task UpdateAsync(bool reprogram, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            var tz = TimeZone;
            var snapshots = new Dictionary<string, IReadOnlyList<PlayoutItem>>();
            foreach (var ch in Plugin.Instance!.Configuration.Channels.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Number)))
            {
                var playout = JsonFile.Load<Playout>(PathFor(ch.Number)) ?? new Playout { Channel = ch.Number };
                var builder = new PlayoutBuilder(ch, tz, _catalog);
                if (reprogram)
                {
                    PlayoutBuilder.TruncateAfterCurrent(playout, now);
                }

                PlayoutBuilder.Prune(playout, now, KeepPast);
                builder.Extend(playout, now, now + Horizon);
                RefreshMetadata(playout);
                JsonFile.Save(PathFor(ch.Number), playout);
                snapshots[ch.Number] = playout.Items.ToList();
            }

            _snapshots = snapshots;
            _logger.LogInformation("Grids updated: {Channels}", string.Join(", ", snapshots.Select(s => $"{s.Key}={s.Value.Count}")));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>What a channel's grid would look like with another configuration (nothing is saved).</summary>
    /// <param name="draft">The channel settings to try.</param>
    /// <param name="span">How far ahead to build.</param>
    /// <returns>The programs from now to <paramref name="span"/>.</returns>
    public IReadOnlyList<PlayoutItem> Preview(ChannelSettings draft, TimeSpan span)
    {
        var now = DateTime.UtcNow;
        var current = JsonFile.Load<Playout>(PathFor(draft.Number));
        var playout = current is null
            ? new Playout { Channel = draft.Number }
            : JsonSerializer.Deserialize<Playout>(JsonSerializer.Serialize(current, JsonFile.Options), JsonFile.Options)!;
        PlayoutBuilder.TruncateAfterCurrent(playout, now);
        new PlayoutBuilder(draft, TimeZone, _catalog).Extend(playout, now, now + span);
        return playout.Items.Where(i => i.EndUtc > now && i.StartUtc < now + span).ToList();
    }

    /// <summary>Forgets a channel's grid (the channel was deleted).</summary>
    /// <param name="channel">Channel number.</param>
    public void Forget(string channel)
    {
        File.Delete(PathFor(channel));
        var copy = new Dictionary<string, IReadOnlyList<PlayoutItem>>(_snapshots);
        copy.Remove(channel);
        _snapshots = copy;
    }

    private static string PathFor(string channel) => Path.Combine(Dir, channel + ".json");

    private IReadOnlyList<PlayoutItem> Snapshot(string channel)
    {
        if (_snapshots.TryGetValue(channel, out var items))
        {
            return items;
        }

        // First request after startup: it is read from disk (the scheduled task will extend it).
        var loaded = JsonFile.Load<Playout>(PathFor(channel))?.Items ?? new List<PlayoutItem>();
        var copy = new Dictionary<string, IReadOnlyList<PlayoutItem>>(_snapshots) { [channel] = loaded };
        _snapshots = copy;
        return loaded;
    }

    /// <summary>
    /// Refreshes the guide and language data of what is already scheduled (without touching times): so a metadata fix or a
    /// language change also shows on what was already in the grid.
    /// </summary>
    private void RefreshMetadata(Playout playout)
    {
        for (int i = 0; i < playout.Items.Count; i++)
        {
            var item = playout.Items[i];
            if (item.IsSlate || _catalog.Template(item.ItemId) is not { } t)
            {
                continue;
            }

            playout.Items[i] = item with
            {
                Title = t.Title,
                EpisodeTitle = t.EpisodeTitle,
                Overview = t.Overview,
                ImageUrl = t.ImageUrl,
                Year = t.Year,
                Language = t.Language,
            };
        }
    }
}
