using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Channels;
using Jellyfin.Plugin.Portalito.Portal;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Gives a "Siguiendo" episode the portal's subtitles, as the channel does. The portal names an episode's subtitle files
/// only in its play call (<c>startPlayVOD</c>), one call per episode, so they're fetched when the episode is about to
/// play -- not for every mirrored episode (500 play calls for one long show) -- and saved next to its .strm as
/// <c>SxxEyy.&lt;lang&gt;.srt</c>, where Jellyfin finds external subtitles.
/// </summary>
/// <remarks>
/// Jellyfin re-probes a .strm on every playback (<c>MediaSourceManager.GetPlaybackMediaSources</c>), listing its folder
/// through a single, never-cleared <see cref="IDirectoryService"/>: once a season folder was listed, files added later
/// stay invisible and that refresh even drops subtitles a scan had found. So after writing the files the folder is
/// evicted from that cache (its private dictionaries, by reflection; if a Jellyfin version renames them the files are
/// still picked up by the next library scan).
/// </remarks>
public sealed class FollowSubtitles
{
    private static readonly TimeSpan CheckedFor = TimeSpan.FromHours(12);
    private static readonly string[] CacheFields = { "_cache", "_fileCache", "_filePathCache" };

    private readonly IPortalitoServicesProvider _services;
    private readonly SubtitleFileCache _files;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<FollowSubtitles> _logger;

    // Episodes already looked at (most have no subtitles at all): one portal call per episode per 12 h, not per playback-info call.
    private readonly ConcurrentDictionary<string, DateTime> _checked = new(StringComparer.Ordinal);

    public FollowSubtitles(IPortalitoServicesProvider services, SubtitleFileCache files, IDirectoryService directoryService, ILogger<FollowSubtitles> logger)
    {
        _services = services;
        _files = files;
        _directoryService = directoryService;
        _logger = logger;
    }

    /// <summary>Makes sure the episode at <paramref name="strmPath"/> has its subtitle files beside it. Never throws for a portal or file problem.</summary>
    public async Task EnsureAsync(string strmPath, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (_checked.TryGetValue(strmPath, out var at) && now - at < CheckedFor)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(strmPath)!;
            var stem = Path.GetFileNameWithoutExtension(strmPath);
            if (HasSubtitles(dir, stem) || FollowFiles.ParseStrm(await File.ReadAllTextAsync(strmPath, cancellationToken).ConfigureAwait(false)) is not { } ids)
            {
                _checked[strmPath] = now;
                return;
            }

            var stream = await _services.Get().Portal.ResolveVodAsync(ids.ContentId, ids.SeriesId, cancellationToken).ConfigureAwait(false);
            var local = await _files.LocalCopiesAsync(stream.Subtitles ?? Array.Empty<SubtitleFile>(), cancellationToken).ConfigureAwait(false);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in local)
            {
                if (FollowFiles.SubtitleFileName(stem, LanguageCodes.ToIso6392(file.Language) ?? file.Language, file.Format, taken) is { } name)
                {
                    File.Copy(file.Url, Path.Combine(dir, name), overwrite: true);
                }
            }

            if (taken.Count > 0)
            {
                Evict(_directoryService, dir);
                _logger.LogInformation("Siguiendo: {Count} subtitle files saved for {Episode}", taken.Count, strmPath);
            }

            _checked[strmPath] = now;
        }
        catch (Exception ex) when (ex is PortalException or IOException or UnauthorizedAccessException or HttpRequestException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Not remembered as checked: the next playback tries again. The episode still plays, without subtitles.
            _logger.LogWarning("Siguiendo: no subtitles for {Episode} this time ({Reason})", strmPath, ex.Message);
        }
    }

    private static bool HasSubtitles(string dir, string stem)
        => Directory.Exists(dir) && Directory.EnumerateFiles(dir, stem + ".*")
            .Any(f => Path.GetExtension(f) is ".srt" or ".vtt" or ".ass" or ".ssa");

    /// <summary>Drops <paramref name="dir"/> and everything under it from a <see cref="DirectoryService"/>'s caches.</summary>
    internal static void Evict(IDirectoryService directoryService, string dir)
    {
        var prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in CacheFields)
        {
            if (directoryService.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(directoryService) is IDictionary cache)
            {
                foreach (var key in cache.Keys.OfType<string>().Where(k => k == dir || k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                {
                    cache.Remove(key);
                }
            }
        }
    }
}
