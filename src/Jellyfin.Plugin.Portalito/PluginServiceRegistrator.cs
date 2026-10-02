using Jellyfin.Plugin.Portalito.Channels;
using Jellyfin.Plugin.Portalito.Live;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
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
        // Known title runtimes (what lets Jellyfin keep playback positions); data path so it survives cache cleanup.
        serviceCollection.AddSingleton(sp => new Catalog.RuntimeStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "portalito", "runtimes.json")));
        // The hidden "Portalito · Siguiendo" library (Next Up): which shows it mirrors, the sync on playback stop, and
        // making Jellyfin stream its .strm episodes itself (see Following/FollowPlaybackFilter).
        serviceCollection.AddSingleton(sp => new Following.FollowStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "portalito", "following.json")));
        serviceCollection.AddSingleton<Following.FollowSubtitles>();
        serviceCollection.AddHostedService<Following.FollowTrigger>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<Following.FollowPlaybackFilter>());
        // Destacado rows listed best-rated first: Jellyfin sorts channel folders with the client's (A-Z) sort.
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<Channels.FeaturedSortFilter>());
        // Name-keyed show ids for the channel's show folders (see Catalog/ShowIndex).
        serviceCollection.AddSingleton(sp => new Catalog.ShowIndex(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "portalito", "shows.json")));
        serviceCollection.AddHostedService<Channels.RemovalMonitor>();
        serviceCollection.AddSingleton<ILiveTvService, PortalitoLiveTvService>();
        serviceCollection.AddSingleton<IChannel, PortalitoVodChannel>();
    }
}
