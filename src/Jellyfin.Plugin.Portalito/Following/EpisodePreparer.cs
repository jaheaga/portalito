using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// Gets a "Siguiendo" episode ready to play before anyone presses play: its subtitle files saved beside the .strm
/// (<see cref="FollowSubtitles"/>) and the .strm probed, which Jellyfin otherwise only does in the playback-info call.
/// Clients pick the audio and subtitle tracks from the episode they already hold, before that call: an episode never
/// played has none, so it started with the default audio and no subtitles, and only a second start got the user's
/// languages (production, 2026-10-20). Used ahead of time by the sync (<see cref="FollowSyncTask"/>) and when an
/// episode's page is opened (<see cref="FollowPlaybackFilter"/>).
/// </summary>
public sealed class EpisodePreparer
{
    private readonly FollowSubtitles _subtitles;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<EpisodePreparer> _logger;

    // Clients fetch an episode several times as its page opens: one preparation each, shared.
    private readonly SingleFlight _flights = new();

    public EpisodePreparer(FollowSubtitles subtitles, IFileSystem fileSystem, ILogger<EpisodePreparer> logger)
    {
        _subtitles = subtitles;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>Whether a library episode is one of our .strm files that hasn't been probed yet (no audio track known).</summary>
    public static bool NeedsPreparing(string? path, IEnumerable<MediaStream> streams)
        => path is not null && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            && !streams.Any(s => s.Type == MediaStreamType.Audio);

    /// <summary>
    /// Prepares <paramref name="episode"/> unless it's ready; returns whether it is ready afterwards. The preparation
    /// isn't cancelled by <paramref name="cancellationToken"/>, only the wait for it: a caller that gives up leaves it
    /// running, so the episode is ready by the next request. Never throws for a portal or probe problem.
    /// </summary>
    public async Task<bool> PrepareAsync(BaseItem episode, CancellationToken cancellationToken)
        => !NeedsPreparing(episode.Path, episode.GetMediaStreams())
            || await _flights.RunAsync(episode.Id.ToString("N"), () => RunAsync(episode), cancellationToken).ConfigureAwait(false);

    private async Task<bool> RunAsync(BaseItem episode)
    {
        await _subtitles.EnsureAsync(episode.Path, CancellationToken.None).ConfigureAwait(false);
        try
        {
            // What Jellyfin does to a .strm at playback (MediaSourceManager.GetPlaybackMediaSources); a fresh directory
            // service, so it sees the subtitle files just written.
            await episode.RefreshMetadata(
                new MetadataRefreshOptions(new DirectoryService(_fileSystem)) { EnableRemoteContentProbe = true, MetadataRefreshMode = MetadataRefreshMode.FullRefresh },
                CancellationToken.None).ConfigureAwait(false);
            return !NeedsPreparing(episode.Path, episode.GetMediaStreams());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // It still plays as before, getting its tracks at the first start; the next sync or page open tries again.
            _logger.LogWarning("Siguiendo: couldn't get {Episode} ready ahead of playback ({Reason})", episode.Path, ex.Message);
            return false;
        }
    }
}
