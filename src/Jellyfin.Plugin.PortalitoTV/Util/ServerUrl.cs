namespace Jellyfin.Plugin.PortalitoTV.Util;

/// <summary>The address ffmpeg and the Live TV tuner use to reach this Jellyfin (the plugin's own stream, M3U and XMLTV endpoints).</summary>
public static class ServerUrl
{
    /// <summary>The address used when none is configured or the configured one isn't an http(s) address.</summary>
    public const string Default = "http://127.0.0.1:8096";

    /// <summary>Gets the configured address, normalized.</summary>
    public static string Current => Normalize(Plugin.Instance?.Configuration.ServerUrl);

    /// <summary>Cleans up a configured address: trimmed, no trailing slash, and only absolute http(s) ones are kept.</summary>
    /// <param name="configured">What the user typed.</param>
    /// <returns>A usable base address, without a trailing slash.</returns>
    public static string Normalize(string? configured)
    {
        var text = (configured ?? string.Empty).Trim().TrimEnd('/');
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? text
            : Default;
    }
}
