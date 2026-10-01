using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>One episode of a mirrored show, as the "Siguiendo" library files describe it.</summary>
public sealed record MirrorEpisode(
    string SeasonId,
    string EpisodeId,
    int Season,
    int Episode,
    string Title,
    string? Plot = null,
    long? RunTimeTicks = null,
    string? ImageUrl = null,
    DateTime? Aired = null);

/// <summary>A show mirrored into the "Siguiendo" library: its own metadata and every episode the portal lists.</summary>
public sealed record MirrorShow(
    string ShowId,
    string Name,
    IReadOnlyList<MirrorEpisode> Episodes,
    string? Plot = null,
    int? Year = null,
    IReadOnlyList<string>? Genres = null,
    string? PosterUrl = null);

/// <summary>
/// The "Siguiendo" library's on-disk layout, as Jellyfin's TV resolver reads it: <c>Show [ptl-id]/tvshow.nfo</c>, one
/// <c>Season NN</c> folder per season, and per episode <c>SxxEyy.strm</c> (the stream URL) plus <c>SxxEyy.nfo</c>
/// (title, numbering, plot, runtime) and optionally <c>SxxEyy-thumb.jpg</c>. Pure: no file system, no Jellyfin.
/// </summary>
public static partial class FollowFiles
{
    /// <summary>The NFO <c>uniqueid</c> type, and the folder-name tag, that mark a show as Portalito's.</summary>
    public const string ProviderKey = "portalito";

    /// <summary>The show's folder: its name made safe for any file system, tagged with its id so two shows never share one.</summary>
    public static string ShowFolder(string name, string showId)
    {
        var safe = UnsafeFileChars().Replace(name, " ");
        safe = MultipleSpaces().Replace(safe, " ").Trim().Trim('.').Trim();
        if (safe.Length > 80)
        {
            safe = safe[..80].TrimEnd();
        }

        return (safe.Length == 0 ? "Serie" : safe) + $" [{ProviderKey}-{showId}]";
    }

    /// <summary>The show id in a folder name written by <see cref="ShowFolder"/>; null for any other name.</summary>
    public static string? ShowIdOfFolder(string? folder)
        => folder is not null && ShowFolderTag().Match(folder) is { Success: true } match ? match.Groups["id"].Value : null;

    /// <summary>
    /// A file always present in the library folder. Jellyfin skips scanning a library folder with no visible entries
    /// ("is inaccessible or empty, skipping"), and then never removes the episodes of a show that just left: they stay
    /// listed, pointing at deleted files (measured on production 2026-10-01). .NET treats dot-files as hidden on Linux,
    /// so it's a plain name; the TV resolver ignores .txt files.
    /// </summary>
    public const string MarkerFile = "portalito-siguiendo.txt";

    public const string MarkerText =
        "Biblioteca \"Portalito · Siguiendo\": la escribe el plugin Portalito con las series que se están viendo, para el\n"
        + "\"A continuación\" de Jellyfin. No la edites a mano: cada sincronización la reescribe.\n";

    public static string SeasonFolder(int season) => "Season " + season.ToString("00", CultureInfo.InvariantCulture);

    public static string EpisodeBase(int season, int episode)
        => "S" + season.ToString("00", CultureInfo.InvariantCulture) + "E" + episode.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>An episode's path inside the show folder, without extension ("Season 01/S01E05").</summary>
    public static string EpisodePath(MirrorEpisode episode)
        => SeasonFolder(episode.Season) + "/" + EpisodeBase(episode.Season, episode.Episode);

    /// <summary>
    /// Every text file of a show, by path relative to the show folder ("/"-separated): <c>tvshow.nfo</c> and each
    /// episode's .strm and .nfo. <paramref name="playUrl"/> gives an episode's stream URL from (contentId, seriesId).
    /// </summary>
    public static IReadOnlyDictionary<string, string> TextFiles(MirrorShow show, Func<string, string, string> playUrl)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["tvshow.nfo"] = ShowNfo(show) };
        foreach (var episode in show.Episodes)
        {
            var path = EpisodePath(episode);
            files[path + ".strm"] = playUrl(episode.EpisodeId, episode.SeasonId) + "\n";
            files[path + ".nfo"] = EpisodeNfo(show, episode);
        }

        return files;
    }

    public static string ShowNfo(MirrorShow show)
    {
        var root = new XElement(
            "tvshow",
            new XElement("title", show.Name),
            Optional("plot", show.Plot),
            Optional("year", show.Year?.ToString(CultureInfo.InvariantCulture)),
            (show.Genres ?? Array.Empty<string>()).Select(g => new XElement("genre", g)),
            new XElement("uniqueid", new XAttribute("type", ProviderKey), new XAttribute("default", "true"), show.ShowId));
        return Serialize(root);
    }

    public static string EpisodeNfo(MirrorShow show, MirrorEpisode episode)
    {
        var root = new XElement(
            "episodedetails",
            new XElement("title", episode.Title),
            new XElement("showtitle", show.Name),
            new XElement("season", episode.Season.ToString(CultureInfo.InvariantCulture)),
            new XElement("episode", episode.Episode.ToString(CultureInfo.InvariantCulture)),
            Optional("plot", episode.Plot),

            // Whole minutes, as Kodi's NFO has it; Jellyfin replaces it with the probed runtime at the first playback.
            Optional("runtime", episode.RunTimeTicks is > 0 and var ticks ? Math.Max(1, (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes)).ToString(CultureInfo.InvariantCulture) : null),
            Optional("aired", episode.Aired?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement("uniqueid", new XAttribute("type", ProviderKey), new XAttribute("default", "true"), episode.EpisodeId));
        return Serialize(root);
    }

    /// <summary>The (contentId, seriesId) a .strm file plays, from the URL <see cref="Proxy.ProxyUrlSigner.PlayUrl"/> wrote; null if it isn't one.</summary>
    public static (string ContentId, string SeriesId)? ParseStrm(string content)
    {
        if (!Uri.TryCreate(content.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var match = PlayPath().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return null;
        }

        var series = uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0] == "series")
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault() ?? string.Empty;
        return (Uri.UnescapeDataString(match.Groups["id"].Value), series);
    }

    /// <summary>
    /// The sidecar name for one of an episode's subtitle files, <c>SxxEyy.&lt;lang&gt;.&lt;format&gt;</c>, which Jellyfin reads
    /// as an external subtitle in that language. Null for a second file in a language already <paramref name="taken"/>
    /// (added to it otherwise).
    /// </summary>
    public static string? SubtitleFileName(string stem, string? language, string format, ISet<string> taken)
    {
        var lang = SafeLanguage().Replace(language ?? string.Empty, string.Empty).ToLowerInvariant();
        lang = lang.Length == 0 ? "und" : lang;
        return taken.Add(lang) ? $"{stem}.{lang}.{format}" : null;
    }

    /// <summary>
    /// The title an episode is shown with. Portal episode names are often just the season's name plus a number
    /// ("Show T1_05"), which says nothing next to the show's name; those become "Episodio 5".
    /// </summary>
    public static string EpisodeTitle(string? name, string? showName, int number)
    {
        var fallback = "Episodio " + number.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var trimmed = name.Trim();
        return !string.IsNullOrWhiteSpace(showName) && trimmed.StartsWith(showName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? fallback
            : trimmed;
    }

    private static XElement? Optional(string name, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);

    private static string Serialize(XElement root)
        => "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n" + root.ToString(SaveOptions.None) + "\n";

    // Characters Windows, macOS or Linux refuse in a file name, plus control characters.
    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1F]")]
    private static partial Regex UnsafeFileChars();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@"[^A-Za-z-]")]
    private static partial Regex SafeLanguage();

    [GeneratedRegex(@"\[portalito-(?<id>[A-Za-z0-9_-]{1,64})\]$")]
    private static partial Regex ShowFolderTag();

    [GeneratedRegex(@"/Portalito/play/(?<id>[^/?]+)$")]
    private static partial Regex PlayPath();
}
