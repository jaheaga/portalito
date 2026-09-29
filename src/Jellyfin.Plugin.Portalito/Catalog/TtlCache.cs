namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>Generic in-memory TTL + LRU-capacity cache, keyed by string.</summary>
public sealed class TtlCache<TValue>
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly LinkedList<string> _lru = new();

    public TtlCache(TimeProvider clock, TimeSpan? ttl = null, int capacity = 32)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        _clock = clock;
        _ttl = ttl ?? TimeSpan.FromHours(6);
        _capacity = capacity;
    }

    /// <summary>Gets the number of live (not necessarily unexpired) entries currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/> if present and not older than the TTL, marking it
    /// most-recently-used. An expired entry is evicted as a side effect of the lookup.
    /// </summary>
    public bool TryGet(string key, out TValue value)
    {
        // Shared off one PortalitoServices singleton across every concurrent request, and Dictionary/LinkedList are
        // not thread-safe.
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && _clock.GetUtcNow() - entry.StoredAt < _ttl)
            {
                Touch(key);
                value = entry.Value;
                return true;
            }

            if (_entries.ContainsKey(key))
            {
                Remove(key);
            }

            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Stores <paramref name="value"/> for <paramref name="key"/>, marking it most-recently-used. Evicts the
    /// least-recently-used entry first if the cache is already at capacity.
    /// </summary>
    public void Set(string key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(key))
            {
                Remove(key);
            }
            else if (_entries.Count >= _capacity)
            {
                EvictLeastRecentlyUsed();
            }

            _entries[key] = new Entry(value, _clock.GetUtcNow());
            _lru.AddLast(key);
        }
    }

    private void Touch(string key)
    {
        _lru.Remove(key);
        _lru.AddLast(key);
    }

    private void Remove(string key)
    {
        _entries.Remove(key);
        _lru.Remove(key);
    }

    private void EvictLeastRecentlyUsed()
    {
        var oldest = _lru.First;
        if (oldest is not null)
        {
            Remove(oldest.Value);
        }
    }

    private readonly record struct Entry(TValue Value, DateTimeOffset StoredAt);
}
