using System.Text.RegularExpressions;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PortalitoTV.Content;

/// <summary>A series with a heart that is (or should be) in "Portalito · Siguiendo".</summary>
/// <param name="Key">The series key (its Portalito id, or "canal:&lt;name&gt;" if it isn't in Siguiendo yet).</param>
/// <param name="Name">The series name.</param>
/// <param name="PortalitoId">Portalito id (null if it has a heart in the channel but hasn't reached Siguiendo yet).</param>
/// <param name="Library">The series in the Siguiendo library (null if it hasn't reached it yet).</param>
public sealed record FavoriteSeries(string Key, string Name, string? PortalitoId, Series? Library);

/// <summary>An episode of Siguiendo.</summary>
/// <param name="Item">The Jellyfin episode.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="RelPath">The .strm path from the show's folder on (the key of the caches).</param>
public sealed record SeriesEpisode(Episode Item, int Season, int Episode, string RelPath);

/// <summary>Reads from Jellyfin what the Portalito plugin materializes: the Siguiendo series, the user's hearts and favorite movies.</summary>
public sealed partial class PortalitoLibrary
{
    /// <summary>The tag every show of the Siguiendo library carries (written by the Portalito plugin).</summary>
    public const string SiguiendoTag = "portalito-siguiendo";

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;

    /// <summary>Initializes a new instance of the <see cref="PortalitoLibrary"/> class.</summary>
    /// <param name="library">The library.</param>
    /// <param name="users">Users.</param>
    /// <param name="userData">Per-user data (favorites).</param>
    public PortalitoLibrary(ILibraryManager library, IUserManager users, IUserDataManager userData)
    {
        _library = library;
        _users = users;
        _userData = userData;
    }

    /// <summary>
    /// Gets the user whose hearts define the content: the configured one, else the first administrator.
    /// </summary>
    public User? FavoritesUser
    {
        get
        {
            var name = Plugin.Instance!.Configuration.FavoritesUser;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return _users.GetUserByName(name.Trim());
            }

            return UsersOf(_users).FirstOrDefault(u => u.HasPermission(PermissionKind.IsAdministrator));
        }
    }

    /// <summary>
    /// The users of a user manager. <c>IUserManager.Users</c> (a property in 10.11.0) became <c>GetUsers()</c> in later 10.11.x
    /// patches. The plugin is built against the 10.11.0 floor and runs on every 10.11.x, so it reaches whichever one exists at
    /// run time (a direct call would throw <see cref="MissingMethodException"/> on a newer server).
    /// </summary>
    /// <param name="manager">The user manager.</param>
    /// <returns>The users (empty if the manager exposes neither member).</returns>
    public static IEnumerable<User> UsersOf(object manager)
    {
        // Look at the interface first (an explicit implementation only shows there), then at the concrete type.
        var contracts = manager is IUserManager ? new[] { typeof(IUserManager), manager.GetType() } : new[] { manager.GetType() };
        foreach (var type in contracts)
        {
            if (type.GetMethod("GetUsers", Type.EmptyTypes) is { } method && method.Invoke(manager, null) is IEnumerable<User> fromMethod)
            {
                return fromMethod;
            }

            if (type.GetProperty("Users")?.GetValue(manager) is IEnumerable<User> fromProperty)
            {
                return fromProperty;
            }
        }

        return Array.Empty<User>();
    }

    // The show folder the Portalito plugin writes: "<name> [portalito-<id>]".
    [GeneratedRegex(@"\[portalito-([A-Za-z0-9_-]{1,64})\]")]
    private static partial Regex FolderId();

    // From the show's folder to the end: "<name> [portalito-<id>]/Season 1/<file>.strm".
    [GeneratedRegex(@"[^/\\]*\[portalito-[A-Za-z0-9_-]{1,64}\][/\\].*$")]
    private static partial Regex FromShowFolder();

    [GeneratedRegex(@"\s*-\s*Espa.ol Latino$", RegexOptions.IgnoreCase)]
    private static partial Regex LatinoSuffix();

    /// <summary>The display name: without the " - Español Latino" suffix.</summary>
    /// <param name="name">The name in the library.</param>
    /// <returns>The clean name.</returns>
    public static string DisplayName(string name) => LatinoSuffix().Replace(name, string.Empty).Trim();

    /// <summary>The key of an episode in the plugin's caches: its path from the show's folder on.</summary>
    /// <param name="path">The full path of the .strm file.</param>
    /// <returns>The path from the show's folder on (the whole path if it isn't under one).</returns>
    public static string RelativeKey(string path) => TryRelativeKey(path, out var key) ? key : path;

    /// <summary>Like <see cref="RelativeKey"/>, and tells whether the path is under a Siguiendo show folder at all.</summary>
    /// <param name="path">The full path of the file.</param>
    /// <param name="key">The path from the show's folder on (the whole path if it isn't under one).</param>
    /// <returns>True if the path is under a "[portalito-id]" show folder.</returns>
    public static bool TryRelativeKey(string path, out string key)
    {
        var m = FromShowFolder().Match(path);
        key = m.Success ? m.Value : path;
        return m.Success;
    }

    /// <summary>The Portalito id of a series of the Siguiendo library.</summary>
    /// <param name="series">The series item.</param>
    /// <returns>The id in upper case, or null if it has none.</returns>
    public static string? PortalitoIdOf(BaseItem series)
    {
        var id = series.GetProviderId("portalito");
        if (!string.IsNullOrEmpty(id))
        {
            return id.ToUpperInvariant();
        }

        var m = FolderId().Match(series.Path ?? string.Empty);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>Whether a series belongs to the Siguiendo library: it carries its tag, or it sits in a "[portalito-id]" folder.</summary>
    /// <param name="series">The series item.</param>
    /// <returns>True if it is a Siguiendo series.</returns>
    public static bool IsSiguiendo(BaseItem series) =>
        series.Tags.Contains(SiguiendoTag, StringComparer.OrdinalIgnoreCase) || FolderId().IsMatch(series.Path ?? string.Empty);

    /// <summary>All the series of Siguiendo (with or without a heart).</summary>
    /// <returns>The series.</returns>
    public IReadOnlyList<Series> SiguiendoSeries() =>
        _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series }, Recursive = true })
            .OfType<Series>()
            .Where(IsSiguiendo)
            .ToList();

    /// <summary>
    /// The series with a heart. The heart can be on the Siguiendo series or on the one in the Portalito channel (those carry
    /// no id, so they are matched by name).
    /// </summary>
    /// <returns>The favorite series, by display name.</returns>
    public IReadOnlyList<FavoriteSeries> FavoriteSeriesList()
    {
        var user = FavoritesUser;
        if (user is null)
        {
            return Array.Empty<FavoriteSeries>();
        }

        var siguiendo = SiguiendoSeries();
        var result = new Dictionary<string, FavoriteSeries>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in siguiendo)
        {
            if (_userData.GetUserData(user, s)?.IsFavorite == true && PortalitoIdOf(s) is { } pid)
            {
                result[pid] = new FavoriteSeries(pid, s.Name, pid, s);
            }
        }

        var channelFavs = _library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                IsFavorite = true,
                Recursive = true,
            })
            .Where(s => !s.ChannelId.Equals(Guid.Empty));
        foreach (var c in channelFavs)
        {
            var lib = siguiendo.FirstOrDefault(s => string.Equals(s.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            var pid = lib is null ? null : PortalitoIdOf(lib);
            var key = pid ?? "canal:" + c.Name;
            if (!result.ContainsKey(key))
            {
                result[key] = new FavoriteSeries(key, c.Name, pid, lib);
            }
        }

        return result.Values.OrderBy(f => DisplayName(f.Name), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The episodes of a Siguiendo series, in order.</summary>
    /// <param name="series">The series.</param>
    /// <returns>The numbered episodes that have a file.</returns>
    public IReadOnlyList<SeriesEpisode> Episodes(Series series) =>
        _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                Recursive = true,
            })
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber is not null && e.IndexNumber is not null && e.Path is not null)
            .Select(e => new SeriesEpisode(e, e.ParentIndexNumber!.Value, e.IndexNumber!.Value, RelativeKey(e.Path)))
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .ToList();

    /// <summary>The movies of the Portalito channel that the user hearted.</summary>
    /// <returns>The favorite movies.</returns>
    public IReadOnlyList<Movie> FavoriteMovies()
    {
        var user = FavoritesUser;
        if (user is null)
        {
            return Array.Empty<Movie>();
        }

        return _library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsFavorite = true,
                Recursive = true,
            })
            .OfType<Movie>()
            .Where(m => !m.ChannelId.Equals(Guid.Empty))
            .ToList();
    }
}
