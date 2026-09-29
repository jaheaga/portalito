using Jellyfin.Plugin.Portalito.Catalog;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class TtlCacheTests
{
    private static (TtlCache<string> Cache, ManualClock Clock) Create(TimeSpan? ttl = null, int capacity = 32)
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000));
        return (new TtlCache<string>(clock, ttl, capacity), clock);
    }

    [Fact]
    public void Round_trips_a_stored_value()
    {
        var (cache, _) = Create();
        cache.Set("avatar", "movie-1");

        Assert.True(cache.TryGet("avatar", out var value));
        Assert.Equal("movie-1", value);
    }

    [Fact]
    public void Missing_key_is_a_miss()
    {
        var (cache, _) = Create();

        Assert.False(cache.TryGet("nope", out _));
    }

    [Fact]
    public void Defaults_to_a_six_hour_ttl()
    {
        var (cache, clock) = Create();
        cache.Set("avatar", "movie-1");

        clock.Now += TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1);
        Assert.True(cache.TryGet("avatar", out _));

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.False(cache.TryGet("avatar", out _));
    }

    [Fact]
    public void Expired_entry_no_longer_counts_toward_size()
    {
        var (cache, clock) = Create(TimeSpan.FromMinutes(1));
        cache.Set("avatar", "movie-1");
        Assert.Equal(1, cache.Count);

        clock.Now += TimeSpan.FromMinutes(2);
        Assert.False(cache.TryGet("avatar", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Setting_an_existing_key_overwrites_the_value_and_refreshes_it()
    {
        var (cache, clock) = Create(TimeSpan.FromMinutes(10));
        cache.Set("avatar", "v1");
        clock.Now += TimeSpan.FromMinutes(5);
        cache.Set("avatar", "v2");
        clock.Now += TimeSpan.FromMinutes(6); // 11 min after the first Set, but only 6 after the second

        Assert.True(cache.TryGet("avatar", out var value));
        Assert.Equal("v2", value);
    }

    [Fact]
    public void Defaults_to_a_thirty_two_entry_capacity()
    {
        var (cache, _) = Create();
        for (var i = 0; i < 32; i++)
        {
            cache.Set($"key{i}", $"v{i}");
        }

        Assert.Equal(32, cache.Count);

        cache.Set("key32", "v32");

        Assert.Equal(32, cache.Count);
        Assert.False(cache.TryGet("key0", out _)); // the least recently used entry was evicted
        Assert.True(cache.TryGet("key32", out _));
    }

    [Fact]
    public void Reading_an_entry_protects_it_from_the_next_eviction()
    {
        var (cache, _) = Create(capacity: 2);
        cache.Set("a", "1");
        cache.Set("b", "2");

        cache.TryGet("a", out _); // "a" is now more-recently-used than "b"
        cache.Set("c", "3"); // evicts the least-recently-used, which is now "b"

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Rejects_a_non_positive_capacity()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TtlCache<string>(new ManualClock(DateTimeOffset.UnixEpoch), capacity: 0));

    [Fact]
    public async Task Concurrent_set_and_get_calls_never_corrupt_or_lose_an_entry()
    {
        // The cache is shared off one singleton across concurrent requests; without its lock this test throws
        // (verified by removing it).
        var cache = new TtlCache<int>(new ManualClock(DateTimeOffset.UnixEpoch), capacity: 64);
        var keys = Enumerable.Range(0, 32).Select(i => $"k{i}").ToArray();

        var writers = keys.Select(k => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                cache.Set(k, i);
            }
        }));
        var readers = keys.Select(k => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                cache.TryGet(k, out _);
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        // Every key was written last with 49 and never evicted (capacity comfortably covers all 32) -- if any
        // concurrent mutation had corrupted the Dictionary/LinkedList, at least one of these would be missing or
        // stuck on a stale value instead of the last one written.
        foreach (var k in keys)
        {
            Assert.True(cache.TryGet(k, out var value), $"{k} was lost");
            Assert.Equal(49, value);
        }

        Assert.Equal(32, cache.Count);
    }
}
