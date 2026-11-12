using Jellyfin.Plugin.Portalito.Configuration;

namespace Jellyfin.Plugin.Portalito.Xtream;

/// <summary>Thrown when the Xtream (IPTV) server can't be reached, the URL is wrong, or it answers unusably.</summary>
public sealed class XtreamException : Exception
{
    public XtreamException(string message)
        : base(message)
    {
    }

    public XtreamException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The Xtream connection settings, taken from the plugin configuration. All three fields come from the operator.</summary>
public sealed record XtreamOptions(string BaseUrl, string Username, string Password)
{
    /// <summary>Gets a value indicating whether all three fields are set, so a client can be built.</summary>
    public bool IsConfigured => BaseUrl.Length > 0 && Username.Length > 0 && Password.Length > 0;

    public static XtreamOptions FromConfiguration(PluginConfiguration config) => new(
        (config.XtreamBaseUrl ?? string.Empty).Trim().TrimEnd('/'),
        (config.XtreamUsername ?? string.Empty).Trim(),

        // Not trimmed: a password can legitimately start or end with a space.
        config.XtreamPassword ?? string.Empty);
}

/// <summary>The account info from <c>player_api.php</c>: the status and connection limits.</summary>
public sealed record XtreamAuth(string Status, int MaxConnections, int ActiveConnections)
{
    public bool IsActive => string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One VOD or series category (<c>category_id</c> and its label).</summary>
public sealed record XtreamCategory(string Id, string Name);

/// <summary>One movie in the bulk VOD list (no TMDB id there -- see <see cref="XtreamVodInfo"/>).</summary>
public sealed record XtreamVodItem(int StreamId, string Name, string? ContainerExtension, string CategoryId);

/// <summary>A movie's detail: its TMDB id and the real container extension.</summary>
public sealed record XtreamVodInfo(int? TmdbId, string? ContainerExtension);

/// <summary>One series in the bulk list; carries its TMDB id and year, so series match exactly.</summary>
public sealed record XtreamSeriesItem(int SeriesId, string Name, int? TmdbId, string CategoryId, int? Year);

/// <summary>One episode of a series, with its season/episode numbers and the id that builds its stream URL.</summary>
public sealed record XtreamEpisode(string Id, int Season, int Episode, string? ContainerExtension, int? TmdbId, string Title);

/// <summary>A series' seasons flattened to an episode list, plus the series' TMDB id.</summary>
public sealed record XtreamSeriesInfo(int? TmdbId, IReadOnlyList<XtreamEpisode> Episodes);
