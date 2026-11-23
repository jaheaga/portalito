using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Portalito.Xtream;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>
/// What's known about a channel's backfilled logo: the URL found for it (or <c>null</c> when a lookup ran and found
/// nothing), and when that was learned. A null URL is a remembered negative -- it stops the channel being looked up
/// again and tells the listing to draw a name tile instead.
/// </summary>
public sealed record LogoRecord(string? Url, DateTime LearnedUtc);

/// <summary>
/// A durable map from a live channel's name to a logo found for it (from a community IPTV logo set), kept so a channel
/// the panel ships without a logo is looked up once and then reused for good. Modelled on <see cref="ShowIndex"/>:
/// name-keyed, saved as JSON next to the plugin's other data, writes batched. Negative results are stored too, so an
/// unmatched channel isn't re-queried on every refresh. There is deliberately no TTL -- a channel's logo barely changes.
/// </summary>
public sealed class ChannelLogoStore
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(5);

    private readonly string? _path;
    private readonly ConcurrentDictionary<string, LogoRecord> _logos;
    private readonly object _saveGate = new();
    private int _savePending;

    /// <param name="path">The JSON file to load and save; null keeps the map in memory only (tests).</param>
    public ChannelLogoStore(string? path)
    {
        _path = path;
        _logos = new ConcurrentDictionary<string, LogoRecord>(Load(path), StringComparer.Ordinal);
    }

    public int Count => _logos.Count;

    /// <summary>The key of a channel by name: normalized the same way titles are matched, hashed so names stay out of the file names.</summary>
    public static string KeyFor(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(XtreamCatalog.NormalizeTitle(name)));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    /// <summary>Looks up what's known for a channel name. <c>true</c> with a record (which may hold a null URL = known no-match).</summary>
    public bool TryGet(string name, out LogoRecord record) => _logos.TryGetValue(KeyFor(name), out record!);

    /// <summary>Records the result of a lookup for a channel name (a found URL, or null for a remembered no-match); returns whether it changed.</summary>
    public bool Record(string name, string? url, DateTime nowUtc)
    {
        var key = KeyFor(name);
        var changed = false;
        _logos.AddOrUpdate(
            key,
            _ =>
            {
                changed = true;
                return new LogoRecord(url, nowUtc);
            },
            (_, known) =>
            {
                if (string.Equals(known.Url, url, StringComparison.Ordinal))
                {
                    return known;
                }

                changed = true;
                return new LogoRecord(url, nowUtc);
            });
        if (changed)
        {
            ScheduleSave();
        }

        return changed;
    }

    /// <summary>Writes pending changes now (tests, and a last chance before shutdown).</summary>
    public void Flush()
    {
        Interlocked.Exchange(ref _savePending, 0);
        Save();
    }

    private void ScheduleSave()
    {
        // One write per burst: a listing can learn many channels at once.
        if (_path is null || Interlocked.Exchange(ref _savePending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(SaveDelay).ContinueWith(_ => Flush(), TaskScheduler.Default);
    }

    private static Dictionary<string, LogoRecord> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, LogoRecord>>(File.ReadAllText(path)) ?? new Dictionary<string, LogoRecord>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Kept aside, never overwritten by a near-empty map; logos are learned again as channels are listed.
            TryMoveAside(path!);
        }

        return new Dictionary<string, LogoRecord>();
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
                File.WriteAllText(temp, JsonSerializer.Serialize(_logos));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not fatal: the map still works for this run and is saved on the next change.
            }
        }
    }
}
