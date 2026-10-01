using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>
/// Gives the "Portalito VOD" channel the images the plugin offers (<see cref="PortalitoVodChannel.GetChannelImage"/>:
/// the logo, the banner) when it lacks any of them. Jellyfin asks a channel for images only when it first creates it,
/// or on a manual "Refresh metadata", so a channel created before 0.1.0.8 never got the logo (measured 2026-10-01 on a
/// test server: no images at all; a forced image refresh brought all three). Only missing images are fetched: one set
/// by hand stays.
/// </summary>
public sealed class ChannelLogoTask : IScheduledTask
{
    private readonly IChannelManager _channelManager;
    private readonly IEnumerable<IChannel> _channels;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ChannelLogoTask> _logger;

    public ChannelLogoTask(IChannelManager channelManager, IEnumerable<IChannel> channels, IFileSystem fileSystem, ILogger<ChannelLogoTask> logger)
    {
        _channelManager = channelManager;
        _channels = channels;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public string Name => "Logo del canal Portalito";

    public string Key => "PortalitoChannelLogo";

    public string Description => "Pone el logo de Portalito en el canal \"Portalito VOD\" si le falta (no reemplaza una imagen puesta a mano).";

    public string Category => "Portalito";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.WeeklyTrigger, DayOfWeek = DayOfWeek.Sunday, TimeOfDayTicks = new TimeSpan(3, 20, 0).Ticks },
    };

    /// <summary>The image types the channel offers that the saved channel doesn't have.</summary>
    public static IReadOnlyList<ImageType> Missing(IEnumerable<ImageType> offered, Func<ImageType, bool> has)
        => offered.Where(t => !has(t)).ToList();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var channels = await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        if (channels.Items.FirstOrDefault(c => c.Name == "Portalito VOD") is not { } channel
            || _channels.OfType<PortalitoVodChannel>().FirstOrDefault() is not { } vod)
        {
            _logger.LogInformation("Channel logo: the Portalito VOD channel isn't registered yet");
            return;
        }

        var missing = Missing(vod.GetSupportedChannelImages(), t => channel.HasImage(t));
        if (missing.Count == 0)
        {
            return;
        }

        // FullRefresh fetches images; without ReplaceAllImages it only fills the types the channel doesn't have.
        await channel.RefreshMetadata(
            new MetadataRefreshOptions(new DirectoryService(_fileSystem)) { ImageRefreshMode = MetadataRefreshMode.FullRefresh },
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Channel logo: asked for {Missing}; the channel now has {Now}",
            string.Join(", ", missing),
            string.Join(", ", vod.GetSupportedChannelImages().Where(t => channel.HasImage(t))));
        progress.Report(100);
    }
}
