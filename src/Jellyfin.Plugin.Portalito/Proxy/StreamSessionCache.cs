using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Portalito.Proxy;

/// <summary>
/// Caches resolved CDN sessions (host + auth + license) per key so segment requests, which arrive many per second,
/// do not each hit the portal. Entries are refreshed shortly before the CDN auth expires, or after a maximum age.
/// </summary>
/// <remarks>
/// Resolving is serialized per key only: a resolve is two portal calls that can take seconds, and with one lock for
/// every key, a channel starting (or re-authing) stalled the segments of every other channel already playing.
/// </remarks>
public sealed class StreamSessionCache<T>
    where T : class
{
    // Past this many entries, stale ones are swept on the next store (VOD keys accumulate one per title played).
    private const int SweepThreshold = 256;

    private readonly TimeProvider _clock;
    private readonly Func<T, DateTimeOffset?> _expiresAt;
    private readonly TimeSpan _refreshMargin;
    private readonly TimeSpan _maxAge;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public StreamSessionCache(TimeProvider clock, Func<T, DateTimeOffset?> expiresAt, TimeSpan refreshMargin, TimeSpan maxAge)
    {
        _clock = clock;
        _expiresAt = expiresAt;
        _refreshMargin = refreshMargin;
        _maxAge = maxAge;
    }

    /// <summary>Returns the cached session for <paramref name="key"/>, resolving a new one when missing or stale.</summary>
    public async Task<T> GetAsync(string key, Func<CancellationToken, Task<T>> resolve, CancellationToken cancellationToken)
    {
        if (TryGetFresh(key, out var fresh))
        {
            return fresh;
        }

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Whoever held the gate before us may have just resolved it.
            if (TryGetFresh(key, out fresh))
            {
                return fresh;
            }

            var value = await resolve(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            _entries[key] = new Entry(value, now, _expiresAt(value));
            if (_entries.Count > SweepThreshold)
            {
                Sweep(now);
            }

            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops <paramref name="stale"/> after the CDN rejected it. Only removes the entry if it is still that same session,
    /// so a burst of concurrent 401s triggers one re-resolve rather than one each. Never waits on a resolve in progress.
    /// </summary>
    public void Invalidate(string key, T stale)
    {
        if (_entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Value, stale))
        {
            // Entry is a plain class, so this removes exactly that entry and not a fresher one stored meanwhile.
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }
    }

    private bool TryGetFresh(string key, out T value)
    {
        if (_entries.TryGetValue(key, out var entry) && !IsStale(entry, _clock.GetUtcNow()))
        {
            value = entry.Value;
            return true;
        }

        value = null!;
        return false;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (IsStale(pair.Value, now))
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private bool IsStale(Entry entry, DateTimeOffset now)
        => now >= entry.ResolvedAt + _maxAge
           || (entry.ExpiresAt is { } expires && now >= expires - _refreshMargin);

    private sealed class Entry
    {
        public Entry(T value, DateTimeOffset resolvedAt, DateTimeOffset? expiresAt)
        {
            Value = value;
            ResolvedAt = resolvedAt;
            ExpiresAt = expiresAt;
        }

        public T Value { get; }

        public DateTimeOffset ResolvedAt { get; }

        public DateTimeOffset? ExpiresAt { get; }
    }
}
