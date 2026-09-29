using Jellyfin.Plugin.Portalito.Channels;
using Jellyfin.Plugin.Portalito.Live;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Portalito;

/// <summary>Registers the plugin's services with Jellyfin's container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IPortalitoServicesProvider>(_ => new PortalitoRuntime(
            () => Plugin.Instance!.Configuration,
            () => Plugin.Instance!.SaveConfiguration(),
            TimeProvider.System,
            () => applicationHost.GetApiUrlForLocalAccess(null, true)));

        // Jellyfin resolves ILiveTvService/IChannel from its own DI container, built from every plugin's
        // RegisterServices contribution -- it does NOT discover them by reflection-scanning plugin
        // assemblies. Confirmed against the working Jellyfin.Xtream reference plugin's own registrator
        // (github.com/Kevinjil/Jellyfin.Xtream/blob/master/Jellyfin.Xtream/PluginServiceRegistrator.cs)
        // after Portalito loaded (assembly + BasePlugin) but neither type appeared in /LiveTv/Info or
        // /Channels on a real Jellyfin 10.11.11 -- the assumption in JellyfinDiscoveryTests.cs (assembly
        // scan + ActivatorUtilities, no explicit registration needed) was wrong.
        serviceCollection.AddSingleton(sp => new Api.ConnectionTester(
            sp.GetRequiredService<IPortalitoServicesProvider>(),
            Api.ConnectionTester.CreateSelfClient()));
        serviceCollection.AddSingleton<IVodTrackProbe, JellyfinVodTrackProbe>();
        serviceCollection.AddSingleton(sp => new SubtitleFileCache(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().CachePath, "portalito-subtitles"),
            new HttpClient { Timeout = SubtitleFileCache.DownloadTimeout }));
        // Data path, not cache path: folders point at these files, and Jellyfin's cache cleanup empties CachePath.
        serviceCollection.AddSingleton(sp => new Collage.CollageService(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "portalito-collages")));
        serviceCollection.AddSingleton<ILiveTvService, PortalitoLiveTvService>();
        serviceCollection.AddSingleton<IChannel, PortalitoVodChannel>();
    }
}
