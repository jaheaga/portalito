using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Portalito.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>
/// The "Portalito · Siguiendo" library itself: where its folder is, creating it, and keeping it out of every user's
/// menus while still feeding their Next Up. Verified in Jellyfin 10.11's source: the per-user "My Media" exclusion
/// (<c>MyMediaExcludes</c>) is read only when listing a user's libraries (<c>UserViewManager.GetUserViews</c>); Next Up
/// (<c>TVSeriesManager</c>) and Continue Watching skip only <c>LatestItemExcludes</c>, which is never touched here.
/// </summary>
public sealed class FollowLibrary
{
    public const string Name = "Portalito · Siguiendo";

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger _logger;

    public FollowLibrary(ILibraryManager library, IUserManager users, ILogger logger)
    {
        _library = library;
        _users = users;
        _logger = logger;
    }

    /// <summary>The library's folder: the configured one, else <c>portalito/siguiendo</c> under Jellyfin's data folder.</summary>
    public static string Root(PluginConfiguration config, string dataPath)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(config.FollowLibraryPath)
            ? Path.Combine(dataPath, "portalito", "siguiendo")
            : config.FollowLibraryPath.Trim());

    /// <summary>Whether <paramref name="path"/> is a file inside the library folder <paramref name="root"/>.</summary>
    public static bool Contains(string root, string? path)
        => path is not null
           && path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// The library's top folder, creating the library first if no library points at <paramref name="root"/>. It's a
    /// TV library that reads only the files the plugin writes: no internet metadata or image fetchers (titles,
    /// numbering and posters come from the .nfo and image files), no trickplay or chapter images (they'd pull whole
    /// remote streams through ffmpeg), no file watcher.
    /// </summary>
    /// <remarks>
    /// Call it once the folder has files: Jellyfin gives an empty library folder no item ("is inaccessible or empty,
    /// skipping", measured 2026-10-01), so a library created empty is re-indexed here once there's something in it.
    /// </remarks>
    public async Task<Folder?> EnsureAsync(string root)
    {
        if (FindByPath(root) is null)
        {
            _logger.LogInformation("Creating the {Library} library at {Root}", Name, root);
            await _library.AddVirtualFolder(Name, CollectionTypeOptions.tvshows, Options(root), refreshLibrary: false).ConfigureAwait(false);
        }

        if (Find(root) is { } library)
        {
            return library;
        }

        await _library.ValidateTopLibraryFolders(CancellationToken.None).ConfigureAwait(false);
        return Find(root);
    }

    /// <summary>
    /// Scans just this library. A library's top folder (<see cref="CollectionFolder"/>) holds no items itself -- its
    /// <c>ValidateChildrenInternal</c> does nothing -- so this walks its physical folders, as Jellyfin's own "scan this
    /// library" does (<c>ProviderManager.RefreshCollectionFolderChildren</c>). A library just created has none yet; the
    /// top-level validation that creates them runs first in that case.
    /// </summary>
    public async Task ScanAsync(Folder library, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        if (library is not CollectionFolder collection)
        {
            await library.ValidateChildren(new Progress<double>(), new MetadataRefreshOptions(directoryService), cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var physical = collection.GetPhysicalFolders().ToList();
        if (physical.Count == 0)
        {
            await _library.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);
            physical = (_library.GetItemById(collection.Id) as CollectionFolder ?? collection).GetPhysicalFolders().ToList();
        }

        if (physical.Count == 0)
        {
            _logger.LogWarning("Siguiendo: Jellyfin has no folder item for {Library} yet; its files are picked up by the next library scan", Name);
        }

        var options = new MetadataRefreshOptions(directoryService);
        foreach (var folder in physical)
        {
            await folder.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
            await folder.ValidateChildren(new Progress<double>(), options, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The library's top folder, if a library points at <paramref name="root"/> and Jellyfin has indexed it.</summary>
    public Folder? Find(string root)
        => FindByPath(root)?.ItemId is { Length: > 0 } id && Guid.TryParse(id, out var guid) ? _library.GetItemById(guid) as Folder : null;

    /// <summary>
    /// Keeps the library hidden from every user's menus (My Media) and lets everyone who can open the Portalito channel
    /// read it -- a user limited to some libraries otherwise wouldn't get its episodes in Next Up. Returns how many
    /// users changed. Users added later are covered on the next run.
    /// </summary>
    public async Task<int> HideAndShareAsync(Guid libraryId, Guid channelId)
    {
        var changed = 0;
        foreach (var user in AllUsers(_users))
        {
            var dirty = false;
            var hidden = user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes);
            if (!hidden.Contains(libraryId))
            {
                user.SetPreference(PreferenceKind.MyMediaExcludes, hidden.Append(libraryId).ToArray());
                dirty = true;
            }

            var seesChannel = user.HasPermission(PermissionKind.EnableAllChannels)
                              || user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels).Contains(channelId);
            var folders = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
            if (seesChannel && !user.HasPermission(PermissionKind.EnableAllFolders) && !folders.Contains(libraryId))
            {
                user.SetPreference(PreferenceKind.EnabledFolders, folders.Append(libraryId).ToArray());
                dirty = true;
            }

            if (dirty)
            {
                await _users.UpdateUserAsync(user).ConfigureAwait(false);
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// Every user. Jellyfin renamed this within 10.11 (the <c>Users</c> property of 10.11.0, which this plugin builds
    /// against, is <c>GetUsers()</c> by 10.11.11), so calling either directly breaks on the other: resolve it at run time.
    /// </summary>
    public static IReadOnlyList<User> AllUsers(IUserManager users)
    {
        var type = typeof(IUserManager);
        var value = type.GetMethod("GetUsers", Type.EmptyTypes) is { } method
            ? method.Invoke(users, null)
            : type.GetProperty("Users")?.GetValue(users);
        return value is IEnumerable<User> list ? list.ToList() : Array.Empty<User>();
    }

    private static LibraryOptions Options(string root)
    {
        var noFetchers = new[] { "Series", "Season", "Episode" }
            .Select(type => new TypeOptions { Type = type, MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() })
            .ToArray();
        return new LibraryOptions
        {
            PathInfos = new[] { new MediaPathInfo(root) },
            TypeOptions = noFetchers,
            EnableRealtimeMonitor = false,
            EnableTrickplayImageExtraction = false,
            ExtractTrickplayImagesDuringLibraryScan = false,
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            EnableEmbeddedTitles = false,
            SaveLocalMetadata = false,
            AutomaticRefreshIntervalDays = 0,
        };
    }

    private VirtualFolderInfo? FindByPath(string root)
        => _library.GetVirtualFolders().FirstOrDefault(f => f.Locations.Any(l => string.Equals(
            Path.GetFullPath(l).TrimEnd(Path.DirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal)));
}
