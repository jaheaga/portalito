using Jellyfin.Plugin.PortalitoTV.Streaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PortalitoTV.LiveTv;

/// <summary>
/// At startup, registers Portalito TV's M3U tuner and XMLTV guide in Jellyfin's Live TV (or fixes their URL if the key or the
/// server address changed). It doesn't touch the other tuners or guides.
/// </summary>
public sealed class JellyfinRegistration : IHostedService
{
    private const string Marker = "/PortalitoTV/";
    private const string FriendlyName = "Portalito TV";

    private readonly ITunerHostManager _tuners;
    private readonly IListingsManager _listings;
    private readonly StreamToken _token;
    private readonly IConfigurationManager _config;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<JellyfinRegistration> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinRegistration"/> class.</summary>
    /// <param name="tuners">Jellyfin's tuner manager.</param>
    /// <param name="listings">Jellyfin's guide manager.</param>
    /// <param name="token">The stream key.</param>
    /// <param name="config">Jellyfin's configuration manager.</param>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="logger">Logger.</param>
    public JellyfinRegistration(ITunerHostManager tuners, IListingsManager listings, StreamToken token,
        IConfigurationManager config, IHostApplicationLifetime lifetime, ILogger<JellyfinRegistration> logger)
    {
        _config = config;
        _lifetime = lifetime;
        _tuners = tuners;
        _listings = listings;
        _token = token;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Jellyfin reads the M3U when the tuner is saved: it has to wait until the HTTP server is listening.
        _lifetime.ApplicationStarted.Register(() => _ = Task.Run(RegisterWithRetriesAsync, CancellationToken.None));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RegisterWithRetriesAsync()
    {
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 5 : 30)).ConfigureAwait(false);
                await EnsureAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning("Registering Portalito TV in Live TV, attempt {Attempt}: {Error}", attempt, e.Message);
            }
        }

        _logger.LogError("Could not register Portalito TV's tuner and guide");
    }

    private async Task EnsureAsync()
    {
        var m3u = GuideController.M3uUrl(_token.Value);
        var tuner = ((LiveTvOptions)_config.GetConfiguration("livetv")).TunerHosts.FirstOrDefault(t => t.Url?.Contains(Marker, StringComparison.Ordinal) == true);
        if (tuner is null || tuner.Url != m3u)
        {
            tuner = await _tuners.SaveTunerHost(new TunerHostInfo
            {
                Id = tuner?.Id,
                Type = "m3u",
                Url = m3u,
                FriendlyName = FriendlyName,
                AllowHWTranscoding = true,
                AllowFmp4TranscodingContainer = false,
                AllowStreamSharing = true,
                FallbackMaxStreamingBitrate = 30_000_000,
                EnableStreamLooping = false,
                TunerCount = 0,
                IgnoreDts = true,
                ReadAtNativeFramerate = false,
            }, dataSourceChanged: true).ConfigureAwait(false);
            _logger.LogInformation("Portalito TV's M3U tuner registered ({Id})", tuner.Id);
        }

        var xmltv = GuideController.XmltvUrl(_token.Value);
        var options = (LiveTvOptions)_config.GetConfiguration("livetv");
        var existing = options.ListingProviders
            .FirstOrDefault(p => p.Path?.Contains(Marker, StringComparison.Ordinal) == true);
        if (existing is null || existing.Path != xmltv || !existing.EnabledTuners.Contains(tuner.Id))
        {
            var saved = await _listings.SaveListingProvider(new ListingsProviderInfo
            {
                Id = existing?.Id,
                Type = "xmltv",
                Path = xmltv,
                EnableAllTuners = false,
                EnabledTuners = new[] { tuner.Id },
                MovieCategories = new[] { "movie" },
                NewsCategories = Array.Empty<string>(),
                SportsCategories = Array.Empty<string>(),
                KidsCategories = Array.Empty<string>(),
                ChannelMappings = Array.Empty<MediaBrowser.Model.Dto.NameValuePair>(),
            }, validateLogin: false, validateListings: false).ConfigureAwait(false);
            _logger.LogInformation("Portalito TV's XMLTV guide registered ({Id})", saved.Id);
        }
    }
}
