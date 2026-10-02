using System.Text.Json;

namespace Jellyfin.Plugin.Portalito.Following;

/// <summary>A show the "Siguiendo" library mirrors: where its files are and when its episode list was last fetched.</summary>
/// <remarks><see cref="Stamp"/> is the <c>FollowSyncTask.FilesStamp</c> the files were written under (null before 0.1.1.6).</remarks>
public sealed record FollowedShow(string ShowId, string Name, string Folder, IReadOnlyList<string> SeasonIds, DateTime AddedUtc, DateTime RefreshedUtc, string? Stamp = null);

/// <summary>
/// The mirrored shows, saved as JSON next to the plugin's other data so a restart doesn't refetch every show. Also the
/// way back from a season (what a channel episode's id names) to its show. Only the sync task writes it.
/// </summary>
public sealed class FollowStore
{
    private readonly string? _path;
    private readonly Dictionary<string, FollowedShow> _shows;

    /// <param name="path">The JSON file to load and save; null keeps the store in memory only (tests).</param>
    public FollowStore(string? path)
    {
        _path = path;
        _shows = Load(path);
    }

    public IReadOnlyCollection<FollowedShow> All => _shows.Values;

    public FollowedShow? Get(string showId) => _shows.GetValueOrDefault(showId);

    /// <summary>The followed show holding <paramref name="seasonId"/>, if any.</summary>
    public FollowedShow? ShowForSeason(string seasonId) => _shows.Values.FirstOrDefault(s => s.SeasonIds.Contains(seasonId, StringComparer.Ordinal));

    /// <summary>The followed show whose files are in <paramref name="folder"/> (a folder name, not a path), if any.</summary>
    public FollowedShow? ShowForFolder(string folder) => _shows.Values.FirstOrDefault(s => string.Equals(s.Folder, folder, StringComparison.Ordinal));

    public void Set(FollowedShow show)
    {
        _shows[show.ShowId] = show;
        Save();
    }

    public void Remove(string showId)
    {
        if (_shows.Remove(showId))
        {
            Save();
        }
    }

    private static Dictionary<string, FollowedShow> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<FollowedShow>>(File.ReadAllText(path)) ?? new List<FollowedShow>();
                return list.ToDictionary(s => s.ShowId, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file only costs refetching the followed shows on the next sync (their folders are rewritten in place).
        }

        return new Dictionary<string, FollowedShow>(StringComparer.Ordinal);
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_shows.Values.OrderBy(s => s.ShowId, StringComparer.Ordinal).ToList()));
        File.Move(temp, _path, overwrite: true);
    }
}
