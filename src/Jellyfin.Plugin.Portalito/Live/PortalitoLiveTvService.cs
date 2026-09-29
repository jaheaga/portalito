using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.Portalito.Live;

/// <summary>
/// Live TV for Jellyfin: the channel list comes from the portal, and every channel's media source is the signed URL of
/// this plugin's own re-signing HLS proxy, so ffmpeg never needs the portal's per-request headers. No portal call is made
/// when a stream is opened; the proxy resolves the CDN session lazily when ffmpeg fetches the playlist.
/// </summary>
public sealed class PortalitoLiveTvService : ILiveTvService
{
    // Jellyfin's guide refresh asks for every channel's programs (800+), one portal call each: bounded so a refresh
    // doesn't hit the portal with a burst (it has returned incomplete answers under bursts before, 2026-09-21).
    private readonly SemaphoreSlim _epgCalls = new(4);
    private readonly IPortalitoServicesProvider _services;

    public PortalitoLiveTvService(IPortalitoServicesProvider services) => _services = services;

    public string Name => "Portalito";

    public string HomePageUrl => string.Empty;

    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var services = _services.Get();
        var payload = (await services.Portal.LiveDataAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Require();
        // Same filter as the channel's own live listing: a code outside the id charset would only ever 400 at the proxy.
        return LiveChannelList.Parse(payload)
            .Where(c => VodItemId.IsSafePart(c.Code))
            .Select(c => new ChannelInfo
            {
                Id = c.Code,
                Name = c.Name,
                Number = c.Number,
                ChannelType = ChannelType.TV,
                // Through the image proxy like every poster: the CDN's "image/jpg" breaks Jellyfin's image cache.
                ImageUrl = c.PosterUrl is { } logo ? services.Signer.ImageUrl(logo, MediaSources.ImageUrlValidity) : null,
                HasImage = c.PosterUrl is null ? null : true,
                IsHD = LiveChannelList.LooksHd(c),
            })
            .ToList();
    }

    /// <summary>
    /// The channel's guide from <c>v3/getProgram</c> (see <see cref="EpgMapper"/>). A channel the portal has no guide
    /// for comes back empty instead of failing, so one bad channel doesn't abort Jellyfin's whole guide refresh.
    /// </summary>
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var services = _services.Get();
        await _epgCalls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await services.Portal.EpgAsync(channelId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.IsSuccess
                ? EpgMapper.Map(response.Data!, channelId, services.EpgTimeZone ?? TimeZoneInfo.Local, startDateUtc, endDateUtc)
                : Array.Empty<ProgramInfo>();
        }
        finally
        {
            _epgCalls.Release();
        }
    }

    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
        => Task.FromResult(BuildMediaSource(channelId));

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        => Task.FromResult(new List<MediaSourceInfo> { BuildMediaSource(channelId) });

    // The proxy holds all stream state, so there is nothing to close or reset.
    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    // ---- recording is not offered by the portal ----

    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<TimerInfo>>(Array.Empty<TimerInfo>());

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<SeriesTimerInfo>>(Array.Empty<SeriesTimerInfo>());

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => throw RecordingUnsupported();

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw RecordingUnsupported();

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken) => throw RecordingUnsupported();

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw RecordingUnsupported();

    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => throw RecordingUnsupported();

    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => throw RecordingUnsupported();

    private MediaSourceInfo BuildMediaSource(string channelId)
        => MediaSources.Live(channelId, _services.Get().Signer.LivePlaylistUrl(channelId, MediaSources.LiveUrlValidity));

    private static NotSupportedException RecordingUnsupported() => new("The Portalito portal does not support recording.");
}
