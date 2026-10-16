using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.Content;

/// <summary>
/// The input URL for ffmpeg. Siguiendo episodes: the permanent signed URL in the .strm. Movies of the Portalito channel: a
/// fresh URL through the media sources (the <c>/Portalito/vod/...</c> one expires). The URLs carry a signature: they are
/// never logged.
/// </summary>
public sealed class InputResolver
{
    private readonly IMediaSourceManager _mediaSources;
    private readonly PortalitoLibrary _library;
    private readonly ILogger<InputResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="InputResolver"/> class.</summary>
    /// <param name="mediaSources">Jellyfin's media sources.</param>
    /// <param name="library">The Portalito library reader.</param>
    /// <param name="logger">Logger.</param>
    public InputResolver(IMediaSourceManager mediaSources, PortalitoLibrary library, ILogger<InputResolver> logger)
    {
        _mediaSources = mediaSources;
        _library = library;
        _logger = logger;
    }

    /// <summary>The probe cache key of an item.</summary>
    /// <param name="item">The episode or movie.</param>
    /// <returns>The episode's path from its show's folder on, or "movie:&lt;id&gt;".</returns>
    public static string ProbeKey(BaseItem item)
    {
        return PortalitoLibrary.TryRelativeKey(item.Path ?? string.Empty, out var key)
            ? key
            : "movie:" + item.Id.ToString("N");
    }

    /// <summary>Finds the URL ffmpeg reads an item from.</summary>
    /// <param name="item">The episode or movie.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The URL or path, or null if it couldn't be resolved.</returns>
    public async Task<string?> ResolveAsync(BaseItem item, CancellationToken ct)
    {
        var path = item.Path;
        if (path is not null && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
            return lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        }

        if (item is Video { IsShortcut: true } video && !string.IsNullOrEmpty(video.ShortcutPath))
        {
            return video.ShortcutPath;
        }

        if (!item.ChannelId.Equals(Guid.Empty))
        {
            try
            {
                var sources = await _mediaSources.GetPlaybackMediaSources(item, _library.FavoritesUser, false, false, ct)
                    .ConfigureAwait(false);
                return sources.FirstOrDefault()?.Path;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning("{Name}: Portalito gave no URL ({Error})", item.Name, e.GetType().Name);
                return null;
            }
        }

        return path;
    }
}
