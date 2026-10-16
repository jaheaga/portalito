using Jellyfin.Plugin.PortalitoTV.Content;
using Jellyfin.Plugin.PortalitoTV.LiveTv;
using Jellyfin.Plugin.PortalitoTV.Media;
using Jellyfin.Plugin.PortalitoTV.Metadata;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using Jellyfin.Plugin.PortalitoTV.Streaming;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PortalitoTV;

/// <summary>Registers the plugin's services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StreamToken>();
        serviceCollection.AddSingleton<SessionRegistry>();
        serviceCollection.AddSingleton<PortalitoLibrary>();
        serviceCollection.AddSingleton<InputResolver>();
        serviceCollection.AddSingleton<ProbeCache>();
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<SubtitleCache>();
        serviceCollection.AddSingleton<ContentCatalog>();
        serviceCollection.AddSingleton<PlayoutManager>();
        serviceCollection.AddSingleton<IPlayoutSource>(sp => sp.GetRequiredService<PlayoutManager>());
        serviceCollection.AddSingleton<ChannelService>();
        serviceCollection.AddSingleton<ItemTranscoder>();
        serviceCollection.AddSingleton<Api.AudioSampler>();
        serviceCollection.AddHostedService<JellyfinRegistration>();
    }
}
