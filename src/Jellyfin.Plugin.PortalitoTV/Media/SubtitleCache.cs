using Jellyfin.Plugin.PortalitoTV.Content;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Media;

/// <summary>
/// Spanish subtitles as a local .srt (ffmpeg's "subtitles" filter needs a file). They are kept in the plugin's own cache; if
/// one isn't there, Jellyfin is asked for it (it gets it from the portal even if the Portalito plugin hasn't saved it).
/// </summary>
public sealed class SubtitleCache
{
    private readonly IMediaSourceManager _mediaSources;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly PortalitoLibrary _library;
    private readonly ILogger<SubtitleCache> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubtitleCache"/> class.</summary>
    /// <param name="mediaSources">Jellyfin's media sources.</param>
    /// <param name="subtitleEncoder">Jellyfin's subtitle encoder.</param>
    /// <param name="library">The Portalito library reader.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleCache(IMediaSourceManager mediaSources, ISubtitleEncoder subtitleEncoder, PortalitoLibrary library,
        ILogger<SubtitleCache> logger)
    {
        _mediaSources = mediaSources;
        _subtitleEncoder = subtitleEncoder;
        _library = library;
        _logger = logger;
    }

    private static string Dir => Path.Combine(Plugin.Instance!.WorkDir, "subs");

    /// <summary>Where the cached .srt of an item is.</summary>
    /// <param name="itemId">The Jellyfin item.</param>
    /// <returns>The file path (it may not exist).</returns>
    public static string PathFor(Guid itemId) => Path.Combine(Dir, itemId.ToString("N") + ".spa.srt");

    /// <summary>The Spanish subtitle among an item's streams (an external one is preferred).</summary>
    /// <param name="streams">The item's media streams.</param>
    /// <returns>The stream, or null if there is none.</returns>
    public static MediaStream? SpanishSubtitle(IEnumerable<MediaStream> streams) =>
        streams.Where(s => s.Type == MediaStreamType.Subtitle)
            .OrderBy(s => s.IsExternal ? 0 : 1)
            .FirstOrDefault(s => (s.Language ?? string.Empty).ToLowerInvariant() is "spa" or "lat" or "es" or "es-419");

    /// <summary>Gets the Spanish subtitle of an item as a local .srt.</summary>
    /// <param name="item">The episode or movie.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The path of the .srt, or null if the item has no Spanish subtitle (or the portal failed).</returns>
    public async Task<string?> GetSpanishSrtAsync(BaseItem item, CancellationToken ct)
    {
        var dest = PathFor(item.Id);
        if (File.Exists(dest))
        {
            return dest;
        }

        Directory.CreateDirectory(Dir);
        var tmp = dest + ".tmp";
        try
        {
            string mediaSourceId = item.Id.ToString("N");
            var sub = SpanishSubtitle(_mediaSources.GetMediaStreams(item.Id));
            if (sub is null)
            {
                // Jellyfin doesn't know it yet: ask for the playback info (Portalito adds its subtitles there).
                var sources = await _mediaSources.GetPlaybackMediaSources(item, _library.FavoritesUser, false, false, ct)
                    .ConfigureAwait(false);
                var source = sources.FirstOrDefault();
                sub = source is null ? null : SpanishSubtitle(source.MediaStreams);
                mediaSourceId = source?.Id ?? mediaSourceId;
            }

            if (sub is null)
            {
                return null;
            }

            await using (var src = await _subtitleEncoder.GetSubtitles(item, mediaSourceId, sub.Index, "srt", 0, 0, false, ct)
                             .ConfigureAwait(false))
            await using (var file = File.Create(tmp))
            {
                await src.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            if (!(await File.ReadAllTextAsync(tmp, ct).ConfigureAwait(false)).Contains("-->", StringComparison.Ordinal))
            {
                File.Delete(tmp);
                _logger.LogWarning("{Name}: the subtitle is empty", item.Name);
                return null;
            }

            File.Move(tmp, dest, overwrite: true);
            return dest;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning("{Name}: could not download the subtitle ({Error})", item.Name, e.GetType().Name);
            File.Delete(tmp);
            return null;
        }
    }
}
