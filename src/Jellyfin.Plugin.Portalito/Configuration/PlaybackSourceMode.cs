namespace Jellyfin.Plugin.Portalito.Configuration;

/// <summary>
/// Which source a title plays from, chosen on the config page (never at playback -- Jellyfin is handed one source).
/// The two "Then" modes serve the primary and silently fall back to the backup only if the primary can't be resolved;
/// <see cref="Auto"/> lets the source-health task pick the primary from which source is currently reachable.
/// </summary>
public enum PlaybackSourceMode
{
    /// <summary>Only the portal. The default -- exactly how the plugin behaved before the IPTV source existed.</summary>
    PortalOnly,

    /// <summary>Only the IPTV (Xtream) account.</summary>
    IptvOnly,

    /// <summary>The portal first; the IPTV account only if the portal can't resolve the stream.</summary>
    PortalThenIptv,

    /// <summary>The IPTV account first; the portal only if the IPTV account can't resolve the stream.</summary>
    IptvThenPortal,

    /// <summary>The source-health task decides the primary (portal when both are up), the other is the backup.</summary>
    Auto,
}

/// <summary>Parses the stored <see cref="PluginConfiguration.PlaybackSource"/> string into a <see cref="PlaybackSourceMode"/>.</summary>
public static class PlaybackSourceModeExtensions
{
    /// <summary>Returns the configured mode, or <see cref="PlaybackSourceMode.PortalOnly"/> when the value is missing or unknown.</summary>
    public static PlaybackSourceMode PlaybackSourceMode(this PluginConfiguration config)
        => Enum.TryParse<PlaybackSourceMode>((config.PlaybackSource ?? string.Empty).Trim(), ignoreCase: true, out var mode)
            ? mode
            : Configuration.PlaybackSourceMode.PortalOnly;
}
