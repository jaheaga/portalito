using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Portal;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>What probing one title's file found.</summary>
public sealed record ProbedTracks(IReadOnlyList<MediaStream> Streams, long? RunTimeTicks, int? Bitrate);

/// <summary>Probes a VOD proxy URL for its tracks. An interface so the channel's tests can fake it.</summary>
public interface IVodTrackProbe
{
    Task<ProbedTracks> ProbeAsync(MediaSourceInfo source, CancellationToken cancellationToken);
}

/// <summary>
/// Probes with Jellyfin's own ffprobe wrapper. Needed because Jellyfin does not probe sources a channel returns from
/// <c>IRequiresMediaInfoCallback</c> (MediaSourceManager.GetPlaybackMediaSources, 10.11.11): with the empty
/// MediaStreams we used to return, clients got no audio picker and ffmpeg just took its default track -- and Portalito
/// files carry several language-tagged audio tracks (measured 2026-09-23: por/spa/spa/eng on one title).
/// </summary>
public sealed class JellyfinVodTrackProbe : IVodTrackProbe
{
    private readonly IMediaEncoder _encoder;

    public JellyfinVodTrackProbe(IMediaEncoder encoder) => _encoder = encoder;

    /// <summary>
    /// ffprobe analysis window for the probe. Jellyfin's server-wide default is sized for transcoding (200 s, seen in
    /// its ffmpeg lines); the probe gets its own, and a copy of the source so playback keeps the server's settings.
    /// </summary>
    internal const int ProbeAnalyzeDurationMs = 10_000;

    public async Task<ProbedTracks> ProbeAsync(MediaSourceInfo source, CancellationToken cancellationToken)
    {
        var probeSource = new MediaSourceInfo
        {
            Id = source.Id,
            Path = source.Path,
            Protocol = source.Protocol,
            Container = source.Container,
            IsRemote = source.IsRemote,
            AnalyzeDurationMs = ProbeAnalyzeDurationMs,
        };
        var info = await _encoder.GetMediaInfo(
            new MediaInfoRequest { MediaSource = probeSource, MediaType = DlnaProfileType.Video, ExtractChapters = false },
            cancellationToken).ConfigureAwait(false);
        return new ProbedTracks(info.MediaStreams ?? new List<MediaStream>(), info.RunTimeTicks, info.Bitrate);
    }
}

/// <summary>Assembles the MediaStreams a VOD source hands Jellyfin: the probed tracks plus the portal's external subtitles.</summary>
public static class VodTracks
{
    /// <summary>The audio language made default when a user has no audio preference of their own.</summary>
    public const string DefaultAudioLanguage = "spa";

    /// <summary>
    /// The probed streams as-is, with the first <see cref="DefaultAudioLanguage"/> audio track marked default (when
    /// there is one) so a user without their own preference gets Spanish, not whatever track comes first -- a user's
    /// own Jellyfin audio preference still wins (MediaSourceManager applies it on top). Then one external stream per
    /// subtitle file, indexed after the probed ones (indexes must be unique within a source).
    /// </summary>
    public static List<MediaStream> Assemble(IReadOnlyList<MediaStream> probed, IReadOnlyList<SubtitleFile> subtitles)
    {
        var streams = probed.ToList();
        var preferredAudio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio
            && string.Equals(s.Language, DefaultAudioLanguage, StringComparison.OrdinalIgnoreCase));
        if (preferredAudio is not null)
        {
            foreach (var audio in streams.Where(s => s.Type == MediaStreamType.Audio))
            {
                audio.IsDefault = ReferenceEquals(audio, preferredAudio);
            }
        }

        var next = streams.Count == 0 ? 0 : streams.Max(s => s.Index) + 1;
        foreach (var subtitle in subtitles)
        {
            var language = LanguageCodes.ToIso6392(subtitle.Language);
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = next++,
                Codec = subtitle.Format,
                Language = language,

                // An unrecognised value still gets shown instead of a bare "Unknown".
                Title = language is null && subtitle.Language.Length > 0 ? subtitle.Language : null,
                IsExternal = true,
                Path = subtitle.Url,
                SupportsExternalStream = true,
            });
        }

        return streams;
    }
}
