using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>A show as listings have seen it: its name and the contentIds of the seasons seen so far.</summary>
public sealed record KnownShow(string Name, IReadOnlyList<string> SeasonIds);

/// <summary>
/// Gives each show one id from its name -- the same in every listing -- and remembers which season contentIds belong to
/// it, so its folder can be opened later. Show folders used to be named after whichever season a listing showed first,
/// so one show became several Series items in Jellyfin, and the old one was deleted with its seasons and episodes when a
/// newer season took the lead (measured on production 2026-10-01: series and "Season Unknown" deletions).
/// Saved as JSON next to the plugin's other data; writes are batched.
/// </summary>
public sealed class ShowIndex
{
    /// <summary>The prefix that tells a name-based show key from a portal contentId in a <c>shw:</c> id.</summary>
    public const string KeyPrefix = "n_";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(5);

    private readonly string? _path;
    private readonly ConcurrentDictionary<string, KnownShow> _shows;
    private readonly object _saveGate = new();
    private int _savePending;

    /// <param name="path">The JSON file to load and save; null keeps the index in memory only (tests).</param>
    public ShowIndex(string? path)
    {
        _path = path;
        _shows = new ConcurrentDictionary<string, KnownShow>(Load(path), StringComparer.Ordinal);
    }

    public int Count => _shows.Count;

    /// <summary>The id key of a show: from its name without the season marker, case and accents as listed.</summary>
    public static string KeyFor(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(SeasonGrouping.WithoutSeason(name)));
        return KeyPrefix + Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    /// <summary>Whether a <c>shw:</c> id's primary part is a name key (and not a legacy season contentId).</summary>
    public static bool IsKey(string primary) => primary.StartsWith(KeyPrefix, StringComparison.Ordinal);

    public bool TryGet(string key, out KnownShow show) => _shows.TryGetValue(key, out show!);

    /// <summary>Records that <paramref name="seasonId"/> belongs to the show named <paramref name="name"/>; returns its key.</summary>
    public string Remember(string name, string seasonId)
    {
        var key = KeyFor(name);
        var changed = false;
        _shows.AddOrUpdate(
            key,
            _ =>
            {
                changed = true;
                return new KnownShow(SeasonGrouping.StripSeasonForDisplay(name), new[] { seasonId });
            },
            (_, known) =>
            {
                if (known.SeasonIds.Contains(seasonId, StringComparer.Ordinal))
                {
                    return known;
                }

                changed = true;
                return known with { SeasonIds = known.SeasonIds.Append(seasonId).ToArray() };
            });
        if (changed)
        {
            ScheduleSave();
        }

        return key;
    }

    /// <summary>Writes pending changes now (tests, and a last chance before shutdown).</summary>
    public void Flush()
    {
        Interlocked.Exchange(ref _savePending, 0);
        Save();
    }

    private void ScheduleSave()
    {
        // One write per burst: a listing remembers dozens of shows at once.
        if (_path is null || Interlocked.Exchange(ref _savePending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(SaveDelay).ContinueWith(_ => Flush(), TaskScheduler.Default);
    }

    private static Dictionary<string, KnownShow> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, KnownShow>>(File.ReadAllText(path)) ?? new Dictionary<string, KnownShow>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Kept aside, never overwritten by a near-empty index; shows are learned again as folders are listed.
            TryMoveAside(path!);
        }

        return new Dictionary<string, KnownShow>();
    }

    private static void TryMoveAside(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do: the next save replaces it.
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        lock (_saveGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_shows));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not fatal: the index still works for this run and is saved on the next change.
            }
        }
    }
}
