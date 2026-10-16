using Jellyfin.Plugin.PortalitoTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PortalitoTV;

/// <summary>24/7 linear TV channels built from the favorites (series and movies) of the Portalito plugin.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin's paths.</param>
    /// <param name="xmlSerializer">Jellyfin's XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        WorkDir = Path.Combine(applicationPaths.DataPath, "portalito-tv");
        Directory.CreateDirectory(WorkDir);
    }

    /// <summary>Gets the running instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the folder with the schedules, caches and subtitles (<c>&lt;data&gt;/portalito-tv</c>).</summary>
    public string WorkDir { get; }

    /// <inheritdoc />
    public override string Name => "Portalito TV";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5f1d6c3e-8a4b-4f0e-9c2d-7b1e3a9d4c60");

    /// <inheritdoc />
    public override string Description => "24/7 linear TV channels (schedule, guide and playout) built from the favorites of the Portalito plugin.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "portalitotv",
            DisplayName = "Portalito TV",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
            EnableInMainMenu = true,
            MenuIcon = "live_tv",
        },
    };
}
