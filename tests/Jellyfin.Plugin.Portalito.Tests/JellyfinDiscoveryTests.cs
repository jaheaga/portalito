using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// CORRECTED 2026-09-21 against a real Jellyfin 10.11.11: Jellyfin does NOT find <see cref="ILiveTvService"/>/
/// <see cref="IChannel"/> by reflection-scanning plugin assemblies -- that was this file's original, untested
/// assumption, and on a live server it meant Portalito loaded (assembly + <see cref="Plugin"/>) but never appeared in
/// <c>/LiveTv/Info</c> or <c>/Channels</c>. Jellyfin resolves them from its own DI container, built from every
/// plugin's <see cref="PluginServiceRegistrator.RegisterServices"/> contribution -- confirmed against the working
/// Jellyfin.Xtream reference plugin's own registrator (github.com/Kevinjil/Jellyfin.Xtream, PluginServiceRegistrator.cs),
/// which explicitly does <c>AddSingleton&lt;ILiveTvService, LiveTvService&gt;()</c> /
/// <c>AddSingleton&lt;IChannel, ...&gt;()</c> for each of its types. These checks now pin THAT contract.
/// </summary>
public class JellyfinDiscoveryTests
{
    private static readonly System.Reflection.Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static IServiceProvider Container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPortalitoServicesProvider>(new WiringHarness());
        services.AddSingleton<Channels.IVodTrackProbe>(new FakeVodTrackProbe());
        services.AddSingleton(new Channels.SubtitleFileCache(Path.GetTempPath(), new HttpClient()));
        services.AddSingleton(new Collage.CollageService(Path.Combine(Path.GetTempPath(), "portalito-collages-discovery")));
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_plugin_exposes_exactly_one_live_tv_service_and_one_channel_type()
    {
        static IEnumerable<Type> Implementing<T>() => PluginAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(T).IsAssignableFrom(t));

        Assert.Equal(new[] { "PortalitoLiveTvService" }, Implementing<ILiveTvService>().Select(t => t.Name));
        Assert.Equal(new[] { "PortalitoVodChannel" }, Implementing<IChannel>().Select(t => t.Name));
    }

    [Fact]
    public void Both_can_be_constructed_by_activator_utilities_from_the_registered_provider()
    {
        var provider = Container();

        Assert.NotNull(ActivatorUtilities.CreateInstance<Live.PortalitoLiveTvService>(provider));
        Assert.NotNull(ActivatorUtilities.CreateInstance<Channels.PortalitoVodChannel>(provider));
    }

    [Fact]
    public void The_vod_channel_also_supports_the_media_info_callback()
    {
        Assert.True(typeof(IRequiresMediaInfoCallback).IsAssignableFrom(typeof(Channels.PortalitoVodChannel)));
    }

    [Fact]
    public void The_registrator_provides_the_services_provider_the_discovered_types_depend_on()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IPortalitoServicesProvider));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>The actual fix: without this, Jellyfin's own container never resolves either type.</summary>
    [Fact]
    public void The_registrator_registers_the_live_tv_service_and_the_vod_channel_with_jellyfins_container()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var liveTv = Assert.Single(services, d => d.ServiceType == typeof(ILiveTvService));
        Assert.Equal(typeof(Live.PortalitoLiveTvService), liveTv.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, liveTv.Lifetime);

        var channel = Assert.Single(services, d => d.ServiceType == typeof(IChannel));
        Assert.Equal(typeof(Channels.PortalitoVodChannel), channel.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, channel.Lifetime);

        // The channel's track probe wraps Jellyfin's own IMediaEncoder, which Jellyfin's container provides.
        var probe = Assert.Single(services, d => d.ServiceType == typeof(Channels.IVodTrackProbe));
        Assert.Equal(typeof(Channels.JellyfinVodTrackProbe), probe.ImplementationType);
    }
}
