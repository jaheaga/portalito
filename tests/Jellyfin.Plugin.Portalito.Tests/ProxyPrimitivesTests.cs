using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Proxy;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

internal sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTimeOffset start) => Now = start;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

public class HlsRewriterTests
{
    private static string Map(string url) => "PROXY(" + url + ")";

    [Fact]
    public void Rewrites_absolute_ts_lines_and_keeps_tags()
    {
        const string playlist =
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:100\n" +
            "#EXTINF:4.000,\n#EXT-SEGMENT:0-100/rd=1786229709\nhttp://tdgao.test/live/chan1/chan1_shisui_1.ts\n" +
            "#EXTINF:4.000,\nhttp://nmbde.test/live/chan1/chan1_shisui_2.ts\n";

        var result = HlsRewriter.Rewrite(playlist, null, Map);

        Assert.Equal(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:100\n" +
            "#EXTINF:4.000,\n#EXT-SEGMENT:0-100/rd=1786229709\nPROXY(http://tdgao.test/live/chan1/chan1_shisui_1.ts)\n" +
            "#EXTINF:4.000,\nPROXY(http://nmbde.test/live/chan1/chan1_shisui_2.ts)\n",
            result);
    }

    [Fact]
    public void Normalizes_crlf_and_adds_a_trailing_newline()
    {
        var result = HlsRewriter.Rewrite("#EXTM3U\r\n#EXTINF:4,\r\nhttp://h.test/a.ts", null, Map);

        Assert.Equal("#EXTM3U\n#EXTINF:4,\nPROXY(http://h.test/a.ts)\n", result);
    }

    [Fact]
    public void Trims_whitespace_around_segment_urls()
    {
        Assert.Equal("PROXY(https://h.test/a.ts)\n", HlsRewriter.Rewrite("  https://h.test/a.ts  \n", null, Map));
    }

    private static readonly Uri PlaylistUri = new("http://cdn.test/live/chan1/index.m3u8?x=1");

    [Theory]
    [InlineData("seg1.ts", "http://cdn.test/live/chan1/seg1.ts")]
    [InlineData("/other/seg1.ts", "http://cdn.test/other/seg1.ts")]
    [InlineData("../chan2/seg1.ts?t=5", "http://cdn.test/live/chan2/seg1.ts?t=5")]
    public void Relative_segments_are_resolved_against_the_playlist_url_then_proxied(string line, string resolved)
    {
        // Left relative, ffmpeg would resolve them against the PROXY's URL and 404 (or fetch without auth).
        Assert.Equal($"PROXY({resolved})\n", HlsRewriter.Rewrite(line, PlaylistUri, Map));
    }

    [Theory]
    [InlineData("http://h.test/segment-without-extension")]
    [InlineData("https://h.test/chunk.aac")]
    [InlineData("http://h.test/part.m4s")]
    public void Every_segment_line_is_proxied_not_just_ts(string line)
    {
        Assert.Equal($"PROXY({line})\n", HlsRewriter.Rewrite(line, PlaylistUri, Map));
    }

    [Fact]
    public void Key_and_map_uris_are_resolved_and_proxied()
    {
        var result = HlsRewriter.Rewrite(
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x01\n#EXT-X-MAP:URI=\"http://h.test/init.mp4\"\n",
            PlaylistUri,
            Map);

        Assert.Equal(
            "#EXT-X-KEY:METHOD=AES-128,URI=\"PROXY(http://cdn.test/live/chan1/key.bin)\",IV=0x01\n#EXT-X-MAP:URI=\"PROXY(http://h.test/init.mp4)\"\n",
            result);
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=NONE")]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key-id\"")]
    [InlineData("#EXT-SEGMENT:0-100/rd=1786229709")]
    [InlineData("#EXT-X-SESSION-DATA:DATA-ID=\"x\",URI=\"http://h.test/data.json\"")]
    public void Tags_without_a_fetchable_media_uri_are_untouched(string line)
    {
        Assert.Equal(line + "\n", HlsRewriter.Rewrite(line, PlaylistUri, Map));
    }

    [Fact]
    public void Non_http_segment_lines_are_untouched()
    {
        Assert.Equal("rtmp://h.test/live\n", HlsRewriter.Rewrite("rtmp://h.test/live", PlaylistUri, Map));
    }

    [Fact]
    public void Without_a_base_url_relative_lines_are_left_as_they_are()
    {
        Assert.Equal("seg1.ts\n", HlsRewriter.Rewrite("seg1.ts", null, Map));
    }

    [Fact]
    public void Best_variant_is_the_highest_bandwidth_resolved_against_the_master()
    {
        const string master =
            "#EXTM3U\n#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=9000000,BANDWIDTH=800000\nlow/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720\nhttp://other.test/hi.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=1200000\nmid/index.m3u8\n";

        Assert.Equal("http://other.test/hi.m3u8", HlsRewriter.BestVariant(master, PlaylistUri));
    }

    [Fact]
    public void A_media_playlist_has_no_variant()
    {
        Assert.Null(HlsRewriter.BestVariant("#EXTM3U\n#EXTINF:4,\nseg1.ts\n", PlaylistUri));
    }

    [Fact]
    public void Empty_playlist_stays_empty()
    {
        Assert.Equal(string.Empty, HlsRewriter.Rewrite(string.Empty, null, Map));
    }
}

public class ProxyUrlSignerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_786_000_000);

    private static (ProxyUrlSigner Signer, ManualClock Clock) Create(string secret = "unit-test-secret")
    {
        var clock = new ManualClock(T0);
        return (new ProxyUrlSigner(secret, "http://127.0.0.1:8096/", clock), clock);
    }

    private static Dictionary<string, string> Query(string url)
    {
        var parsed = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        return parsed.AllKeys.ToDictionary(k => k!, k => parsed[k]!);
    }

    [Fact]
    public void Live_url_has_the_expected_shape_and_verifies()
    {
        var (signer, _) = Create();

        var url = signer.LivePlaylistUrl("chan_1", TimeSpan.FromHours(12));

        Assert.StartsWith("http://127.0.0.1:8096/Portalito/live/chan_1.m3u8?e=", url);
        var q = Query(url);
        Assert.Equal(T0.AddHours(12).ToUnixTimeSeconds(), long.Parse(q["e"]));
        Assert.True(signer.VerifyLivePlaylist("chan_1", long.Parse(q["e"]), q["s"]));
    }

    [Fact]
    public void Segment_url_round_trips_a_complex_upstream_url()
    {
        var (signer, _) = Create();
        const string upstream = "http://tdgao.test/live/chan1/chan1_shisui_1.ts?a=b&c=d%20e";

        var q = Query(signer.SegmentUrl("chan1", upstream, TimeSpan.FromMinutes(10)));

        Assert.Equal("chan1", q["c"]);
        Assert.Equal(upstream, q["u"]);
        Assert.True(signer.VerifySegment(q["c"], q["u"], long.Parse(q["e"]), q["s"]));
    }

    [Fact]
    public void Vod_url_omits_series_when_empty_and_verifies_both_forms()
    {
        var (signer, _) = Create();

        var plain = signer.VodUrl("ABC123", string.Empty, TimeSpan.FromHours(6));
        var plainQuery = Query(plain);
        Assert.DoesNotContain("series=", plain);
        Assert.StartsWith("http://127.0.0.1:8096/Portalito/vod/ABC123?e=", plain);
        Assert.True(signer.VerifyVod("ABC123", string.Empty, long.Parse(plainQuery["e"]), plainQuery["s"]));

        var series = Query(signer.VodUrl("ABC123", "SER9", TimeSpan.FromHours(6)));
        Assert.Equal("SER9", series["series"]);
        Assert.True(signer.VerifyVod("ABC123", series["series"], long.Parse(series["e"]), series["s"]));
    }

    [Fact]
    public void Image_url_round_trips_a_complex_upstream_url_and_rejects_tampering()
    {
        var (signer, _) = Create();
        const string upstream = "https://cdn.test/public/images/a-b.jpg?size=262*370&x=y%20z";

        var url = signer.ImageUrl(upstream, TimeSpan.FromDays(30));

        Assert.StartsWith("http://127.0.0.1:8096/Portalito/img?u=", url);
        var q = Query(url);
        Assert.Equal(upstream, q["u"]);
        Assert.True(signer.VerifyImage(upstream, long.Parse(q["e"]), q["s"]));
        Assert.False(signer.VerifyImage("https://cdn.test/other.jpg", long.Parse(q["e"]), q["s"]));
    }

    [Fact]
    public void Tampering_with_any_parameter_is_rejected()
    {
        var (signer, _) = Create();
        var q = Query(signer.SegmentUrl("chan1", "http://h.test/a.ts", TimeSpan.FromMinutes(10)));
        var e = long.Parse(q["e"]);

        Assert.True(signer.VerifySegment("chan1", "http://h.test/a.ts", e, q["s"]));
        Assert.False(signer.VerifySegment("chan2", "http://h.test/a.ts", e, q["s"]));
        Assert.False(signer.VerifySegment("chan1", "http://evil.test/a.ts", e, q["s"]));
        Assert.False(signer.VerifySegment("chan1", "http://h.test/a.ts", e + 1, q["s"]));
        Assert.False(signer.VerifySegment("chan1", "http://h.test/a.ts", e, q["s"][..^1] + (q["s"][^1] == '0' ? '1' : '0')));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothex!")]
    [InlineData("abcd")]
    public void Malformed_signatures_are_rejected(string? signature)
    {
        var (signer, _) = Create();
        Assert.False(signer.VerifyLivePlaylist("chan1", T0.AddHours(1).ToUnixTimeSeconds(), signature));
    }

    [Fact]
    public void Expired_urls_are_rejected()
    {
        var (signer, clock) = Create();
        var q = Query(signer.LivePlaylistUrl("chan1", TimeSpan.FromMinutes(1)));
        var e = long.Parse(q["e"]);

        clock.Now = T0.AddSeconds(59);
        Assert.True(signer.VerifyLivePlaylist("chan1", e, q["s"]));

        clock.Now = T0.AddSeconds(61);
        Assert.False(signer.VerifyLivePlaylist("chan1", e, q["s"]));
    }

    [Fact]
    public void A_signature_for_one_endpoint_does_not_verify_on_another()
    {
        var (signer, _) = Create();
        var live = Query(signer.LivePlaylistUrl("ABC", TimeSpan.FromHours(1)));
        var e = long.Parse(live["e"]);

        Assert.False(signer.VerifyVod("ABC", string.Empty, e, live["s"]));
        Assert.False(signer.VerifySegment("ABC", string.Empty, e, live["s"]));
    }

    [Fact]
    public void Different_secrets_do_not_verify_each_other()
    {
        var (a, _) = Create("secret-a");
        var (b, _) = Create("secret-b");
        var q = Query(a.LivePlaylistUrl("chan1", TimeSpan.FromHours(1)));

        Assert.False(b.VerifyLivePlaylist("chan1", long.Parse(q["e"]), q["s"]));
    }

    [Fact]
    public void Part_boundaries_cannot_be_shifted()
    {
        var (signer, _) = Create();
        var q = Query(signer.SegmentUrl("a", "b:c", TimeSpan.FromHours(1)));

        Assert.False(signer.VerifySegment("a:b", "c", long.Parse(q["e"]), q["s"]));
    }

    [Fact]
    public void Ping_url_verifies_for_its_own_nonce_only_and_expires()
    {
        var clock = new ManualClock(T0);
        var signer = new ProxyUrlSigner("s", "http://127.0.0.1:8096/", clock);
        var url = signer.PingUrl("abc123", TimeSpan.FromMinutes(1));
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.StartsWith("http://127.0.0.1:8096/Portalito/ping?", url, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:8096", signer.BaseUrl);
        Assert.True(signer.VerifyPing("abc123", long.Parse(q["e"]!), q["s"]));
        Assert.False(signer.VerifyPing("other", long.Parse(q["e"]!), q["s"]));

        // A ping signature is not a live-playlist signature for a channel of the same name.
        Assert.False(signer.VerifyLivePlaylist("abc123", long.Parse(q["e"]!), q["s"]));

        clock.Now = T0.AddMinutes(2);
        Assert.False(signer.VerifyPing("abc123", long.Parse(q["e"]!), q["s"]));
    }

    [Fact]
    public void Empty_secret_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ProxyUrlSigner(string.Empty, "http://x", TimeProvider.System));
    }
}

public class StreamSessionCacheTests
{
    private sealed record Session(string Id, DateTimeOffset? Expires);

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_786_000_000);

    private static StreamSessionCache<Session> Create(ManualClock clock)
        => new(clock, s => s.Expires, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));

    [Fact]
    public async Task Caches_until_stale()
    {
        var clock = new ManualClock(T0);
        var cache = Create(clock);
        var calls = 0;
        Task<Session> Resolve(CancellationToken ct) => Task.FromResult(new Session("s" + ++calls, null));

        var first = await cache.GetAsync("k", Resolve, default);
        var second = await cache.GetAsync("k", Resolve, default);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        var cache = Create(new ManualClock(T0));
        var calls = 0;
        Task<Session> Resolve(CancellationToken ct) => Task.FromResult(new Session("s" + ++calls, null));

        await cache.GetAsync("a", Resolve, default);
        await cache.GetAsync("b", Resolve, default);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Refreshes_before_the_cdn_auth_expires()
    {
        var clock = new ManualClock(T0);
        var cache = Create(clock);
        var calls = 0;
        Task<Session> Resolve(CancellationToken ct) => Task.FromResult(new Session("s" + ++calls, clock.Now.AddMinutes(30)));

        await cache.GetAsync("k", Resolve, default);
        clock.Now = T0.AddMinutes(24);
        await cache.GetAsync("k", Resolve, default);
        Assert.Equal(1, calls);

        clock.Now = T0.AddMinutes(26); // within the 5 minute margin of expiry
        var refreshed = await cache.GetAsync("k", Resolve, default);
        Assert.Equal(2, calls);
        Assert.Equal("s2", refreshed.Id);
    }

    [Fact]
    public async Task Refreshes_after_max_age_when_no_expiry_is_known()
    {
        var clock = new ManualClock(T0);
        var cache = Create(clock);
        var calls = 0;
        Task<Session> Resolve(CancellationToken ct) => Task.FromResult(new Session("s" + ++calls, null));

        await cache.GetAsync("k", Resolve, default);
        clock.Now = T0.AddHours(1);
        await cache.GetAsync("k", Resolve, default);
        Assert.Equal(1, calls);

        clock.Now = T0.AddHours(2);
        await cache.GetAsync("k", Resolve, default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Invalidate_only_drops_the_session_that_was_rejected()
    {
        var cache = Create(new ManualClock(T0));
        var calls = 0;
        Task<Session> Resolve(CancellationToken ct) => Task.FromResult(new Session("s" + ++calls, null));

        var first = await cache.GetAsync("k", Resolve, default);
        cache.Invalidate("k", first);
        var second = await cache.GetAsync("k", Resolve, default);
        Assert.NotSame(first, second);

        // A late 401 for the old session must not evict the fresh one.
        cache.Invalidate("k", first);
        var third = await cache.GetAsync("k", Resolve, default);
        Assert.Same(second, third);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Failed_resolves_are_not_cached()
    {
        var cache = Create(new ManualClock(T0));
        var calls = 0;
        Task<Session> Resolve(CancellationToken ct)
            => ++calls == 1 ? throw new InvalidOperationException("portal down") : Task.FromResult(new Session("ok", null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("k", Resolve, default));
        var session = await cache.GetAsync("k", Resolve, default);

        Assert.Equal("ok", session.Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_resolve()
    {
        var cache = Create(new ManualClock(T0));
        var calls = 0;
        async Task<Session> Resolve(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50, ct);
            return new Session("s", null);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => cache.GetAsync("k", Resolve, default)));

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task A_slow_resolve_for_one_key_does_not_block_another_key()
    {
        var cache = Create(new ManualClock(T0));
        var slow = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = cache.GetAsync("a", _ => slow.Task, default);

        // Before: one lock for every key, so this waited for channel "a"'s portal round trip.
        var other = await cache.GetAsync("b", _ => Task.FromResult(new Session("b", null)), default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("b", other.Id);
        Assert.False(stuck.IsCompleted);
        slow.SetResult(new Session("a", null));
        Assert.Equal("a", (await stuck).Id);
    }

    [Fact]
    public async Task Invalidate_does_not_wait_for_a_resolve_in_progress()
    {
        var clock = new ManualClock(T0);
        var cache = Create(clock);
        await cache.GetAsync("a", _ => Task.FromResult(new Session("old", null)), default);
        var slow = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = await cache.GetAsync("a", _ => Task.FromResult(new Session("unused", null)), default);
        cache.Invalidate("a", first);
        var refreshing = cache.GetAsync("a", _ => slow.Task, default);

        // Invalidate must not wait on the resolve in progress (it used to block a thread-pool thread on the lock).
        var invalidate = Task.Run(() => cache.Invalidate("a", first));
        await invalidate.WaitAsync(TimeSpan.FromSeconds(5));

        slow.SetResult(new Session("new", null));
        Assert.Equal("new", (await refreshing).Id);
        Assert.Equal("new", (await cache.GetAsync("a", _ => Task.FromResult(new Session("unused", null)), default)).Id);
    }

    [Fact]
    public async Task Sweeping_stale_entries_keeps_fresh_ones_cached()
    {
        var clock = new ManualClock(T0);
        var cache = Create(clock);
        for (var i = 0; i < 300; i++)
        {
            await cache.GetAsync("old" + i, _ => Task.FromResult(new Session("s", null)), default);
        }

        clock.Now = T0.AddHours(3);
        var calls = 0;
        await cache.GetAsync("fresh", _ => Task.FromResult(new Session("f" + ++calls, null)), default);

        // Swept entries simply resolve again; the point is that nothing breaks and fresh keys stay cached.
        await cache.GetAsync("fresh", _ => Task.FromResult(new Session("f" + ++calls, null)), default);
        Assert.Equal(1, calls);
    }

public class StreamSessionGateTests
{
    private sealed record Session(string Id);

    [Fact]
    public async Task Gates_of_swept_sessions_are_dropped_too()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_786_000_000));
        var cache = new StreamSessionCache<Session>(clock, _ => null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        for (var i = 0; i < 300; i++)
        {
            await cache.GetAsync("old" + i, _ => Task.FromResult(new Session("x")), default);
        }

        clock.Now += TimeSpan.FromHours(3); // all stale
        for (var i = 0; i < 300; i++)
        {
            await cache.GetAsync("new" + i, _ => Task.FromResult(new Session("y")), default);
        }

        Assert.True(cache.GateCount < 400, $"{cache.GateCount} gates kept"); // the 300 old titles' gates went with them
    }
}
}
