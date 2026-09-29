using System.Net;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Portal;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class StreamResolutionTests
{
    private const string LiveToken = "00112233445566778899AABBCCDDEEFF";

    private static PortalOptions Options() => new()
    {
        TripleDesKeyHex = PortalCipherTests.TestKeyHex,
        Hosts = new[] { "host-a.test" },
        AppId = "com.example.app",
        ApkVersion = "12345",
        DeviceSn = "TESTSN0001",
        Portal = "portal1",
        ApiBasePath = "/api/core/",
        ApkVer = "1000",
        SpkgVer = "pkg-1",
        PortalUserAgent = "test-ua/1.0",
        CdnUserAgent = "test-cdn-ua/1.0",
        LoginPasswordSalt = "testsalt",
        LiveColumnCode = "live_all",
        AllChannelsColumnId = 999,
    };

    private static JsonObject Cdn(string tag, string mainAddr, params string[] urls) => new()
    {
        ["tag"] = tag,
        ["main_addr"] = mainAddr,
        ["url_list"] = new JsonArray(urls.Select(u => (JsonNode?)new JsonObject { ["url"] = u }).ToArray()),
    };

    private static string Slb(FakePortalTransport t, params JsonObject[] cdns)
        => t.Ok(new JsonObject { ["cdn_list"] = new JsonArray(cdns.Select(c => (JsonNode?)c).ToArray()) });

    private static FakePortalTransport Portal(Func<FakePortalTransport, FakeRequest, string> route)
    {
        var transport = new FakePortalTransport();
        transport.Handler = r => r.Path == "v8/active"
            ? transport.Ok(new JsonObject { ["userId"] = "u1", ["userToken"] = "tok1" })
            : route(transport, r);
        return transport;
    }

    [Fact]
    public async Task ResolveLive_picks_the_cfl_entry_and_extracts_token_host_license_and_expiry()
    {
        var transport = Portal((t, r) => r.Path switch
        {
            "v4/startPlayLive" => t.Ok(new JsonObject
            {
                ["liveAddressList"] = new JsonArray(new JsonObject { ["playCode"] = "chan1", ["license"] = "LIC-live" }),
            }),
            "v14/getSlbInfo" => Slb(
                t,
                Cdn("live", "http://nvuos.test", "user_id=1&sign_type=cs&token=" + new string('A', 32)),
                Cdn("vod", "http://vod.test", "sign_type=cfl&token=" + new string('B', 32)),
                Cdn(
                    "live",
                    "http://niguof.test/live/",
                    "sign_type=goog&token=" + new string('C', 32),
                    $"user_id=1&sign_type=cfl&link=cf&expired=1786230000&token={LiveToken}")),
            _ => FakePortalTransport.Error("x", r.Path),
        });
        var client = new PortalClient(Options(), transport);

        var live = await client.ResolveLiveAsync("chan1");

        Assert.Equal("chan1", live.ChannelCode);
        Assert.Equal("niguof.test", live.CflHost);
        Assert.Equal($"user_id=1&sign_type=cfl&link=cf&expired=1786230000&token={LiveToken}", live.CflAuthUrl);
        Assert.Equal("LIC-live", live.License);
        Assert.Equal(LiveToken, live.Token);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786230000), live.ExpiresAt);

        var slbBody = transport.Requests.Single(r => r.Path == "v14/getSlbInfo").Body;
        Assert.Equal("merge", slbBody["type"]!.GetValue<string>());
        Assert.Equal("chan1", slbBody["liveCodeList"]![0]!.GetValue<string>());
        Assert.Equal("12345", slbBody["appVer"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResolveLive_keeps_every_cfl_cdn_as_failover_alternates_dropping_tokenless_ones()
    {
        var tokenB = new string('B', 32);
        var transport = Portal((t, r) => r.Path switch
        {
            "v4/startPlayLive" => t.Ok(new JsonObject
            {
                ["liveAddressList"] = new JsonArray(new JsonObject { ["playCode"] = "chan1", ["license"] = "LIC-live" }),
            }),
            "v14/getSlbInfo" => Slb(
                t,
                Cdn("live", "http://one.test", $"sign_type=cfl&token={LiveToken}"),
                Cdn("live", "http://two.test", "sign_type=cfl&notoken=1"), // no 32-hex token: dropped
                Cdn("live", "http://three.test", $"sign_type=cfl&token={tokenB}")),
            _ => FakePortalTransport.Error("x", r.Path),
        });
        var client = new PortalClient(Options(), transport);

        var live = await client.ResolveLiveAsync("chan1");

        Assert.Equal("one.test", live.CflHost); // primary is the first cfl
        Assert.Equal(new[] { "one.test", "three.test" }, live.Cdns.Select(c => c.Host));
        Assert.Equal(new[] { LiveToken, tokenB }, live.Cdns.Select(c => c.Token));
    }

    [Fact]
    public async Task ResolveLive_takes_play_code_and_license_from_the_same_entry_preferring_the_first_complete_one()
    {
        var transport = Portal((t, r) => r.Path switch
        {
            "v4/startPlayLive" => t.Ok(new JsonObject
            {
                ["liveAddressList"] = new JsonArray(
                    new JsonObject { ["license"] = "LIC-no-playcode" },
                    new JsonObject { ["playCode"] = "cx-2EF7E10E40C1ac19D6A9F3ED4CD2", ["license"] = "LIC-paired" },
                    new JsonObject { ["playCode"] = "other", ["license"] = "LIC-other" }),
            }),
            _ => Slb(t, Cdn("live", "http://x.test", $"sign_type=cfl&token={LiveToken}")),
        });
        var client = new PortalClient(Options(), transport);

        var live = await client.ResolveLiveAsync("cx-EXAMPLE");

        // Never crossed: the license authorizes exactly one signal, the one its own entry names.
        Assert.Equal("cx-2EF7E10E40C1ac19D6A9F3ED4CD2", live.PlayCode);
        Assert.Equal("LIC-paired", live.License);
        Assert.Equal("cx-2EF7E10E40C1ac19D6A9F3ED4CD2", live.CdnCode);
    }

    [Fact]
    public async Task ResolveLive_with_no_play_code_anywhere_uses_the_first_license_and_the_channel_code()
    {
        var transport = Portal((t, r) => r.Path switch
        {
            "v4/startPlayLive" => t.Ok(new JsonObject
            {
                ["liveAddressList"] = new JsonArray(new JsonObject { ["license"] = "LIC-1" }, new JsonObject { ["license"] = "LIC-2" }),
            }),
            _ => Slb(t, Cdn("live", "http://x.test", $"sign_type=cfl&token={LiveToken}")),
        });
        var client = new PortalClient(Options(), transport);

        var live = await client.ResolveLiveAsync("chan1");

        Assert.Null(live.PlayCode);
        Assert.Equal("LIC-1", live.License);
        Assert.Equal("chan1", live.CdnCode);
    }

    [Fact]
    public async Task ResolveLive_accepts_a_cfl_entry_marked_by_its_own_sign_type_field()
    {
        var transport = Portal((t, r) => r.Path switch
        {
            "v4/startPlayLive" => t.Ok(new JsonObject
            {
                ["liveAddressList"] = new JsonArray(new JsonObject { ["license"] = "LIC" }),
            }),
            _ => t.Ok(new JsonObject
            {
                ["cdn_list"] = new JsonArray(new JsonObject
                {
                    ["tag"] = "live",
                    ["main_addr"] = "http://field.test",
                    ["url_list"] = new JsonArray(new JsonObject { ["url"] = $"user_id=1&token={LiveToken}", ["sign_type"] = "cfl" }),
                }),
            }),
        });
        var client = new PortalClient(Options(), transport);

        var live = await client.ResolveLiveAsync("chan1");

        Assert.Equal("field.test", live.CflHost);
        Assert.Equal(LiveToken, live.Token);
    }

    [Fact]
    public async Task ResolveLive_without_a_cfl_live_entry_throws()
    {
        var transport = Portal((t, r) => r.Path switch
        {
            "v4/startPlayLive" => t.Ok(new JsonObject
            {
                ["liveAddressList"] = new JsonArray(new JsonObject { ["license"] = "LIC" }),
            }),
            _ => Slb(t, Cdn("live", "http://x.test", "sign_type=cs&token=" + new string('A', 32))),
        });
        var client = new PortalClient(Options(), transport);

        var ex = await Assert.ThrowsAsync<PortalException>(() => client.ResolveLiveAsync("chan1"));
        Assert.Contains("cfl", ex.Message);
    }

    [Fact]
    public async Task ResolveLive_without_a_license_throws()
    {
        var transport = Portal((t, r) => t.Ok(new JsonObject { ["liveAddressList"] = new JsonArray() }));
        var client = new PortalClient(Options(), transport);

        await Assert.ThrowsAsync<PortalException>(() => client.ResolveLiveAsync("chan1"));
    }

    [Fact]
    public async Task ResolveLive_without_a_token_in_the_auth_url_throws()
    {
        var transport = Portal((t, r) => r.Path == "v4/startPlayLive"
            ? t.Ok(new JsonObject { ["liveAddressList"] = new JsonArray(new JsonObject { ["license"] = "LIC" }) })
            : Slb(t, Cdn("live", "http://x.test", "sign_type=cfl&token=short")));
        var client = new PortalClient(Options(), transport);

        await Assert.ThrowsAsync<PortalException>(() => client.ResolveLiveAsync("chan1"));
    }

    [Fact]
    public async Task ResolveVod_tolerates_numeric_fields()
    {
        // GetValue<string>() on a JSON number throws InvalidOperationException, which the proxy turned into a bare 500.
        var transport = Portal((t, r) => r.Path switch
        {
            "v10/startPlayVOD" => t.Ok(new JsonObject
            {
                ["episodeList"] = new JsonArray(new JsonObject
                {
                    ["totalMovieList"] = new JsonArray(new JsonObject
                    {
                        ["movieList"] = new JsonArray(new JsonObject
                        {
                            ["contentId"] = 987654321,
                            ["licenseList"] = new JsonArray(new JsonObject { ["license"] = "LIC-vod" }),
                        }),
                    }),
                }),
            }),
            "v14/getSlbInfo" => Slb(
                t,
                new JsonObject { ["tag"] = 7, ["main_addr"] = "http://other.test", ["url_list"] = new JsonArray() },
                Cdn("vod", "http://vodcdn.test", "sign_type=cfl&token=" + new string('B', 32))),
            _ => FakePortalTransport.Error("x", r.Path),
        });
        var client = new PortalClient(Options(), transport);

        var vod = await client.ResolveVodAsync("content1");

        Assert.Equal("987654321", vod.MediaCode);
        Assert.Equal("vodcdn.test", vod.Host);
    }

    [Fact]
    public async Task ResolveVod_extracts_media_code_license_and_cfl_entry()
    {
        var transport = Portal((t, r) => r.Path switch
        {
            "v10/startPlayVOD" => t.Ok(new JsonObject
            {
                ["episodeList"] = new JsonArray(new JsonObject
                {
                    ["totalMovieList"] = new JsonArray(new JsonObject
                    {
                        ["movieList"] = new JsonArray(new JsonObject
                        {
                            ["contentId"] = "MEDIA123",
                            ["licenseList"] = new JsonArray(new JsonObject { ["license"] = "LIC-vod" }),
                        }),
                    }),
                }),
            }),
            "v14/getSlbInfo" => Slb(
                t,
                Cdn("live", "http://live.test", "sign_type=cfl&token=" + new string('A', 32)),
                Cdn("vod", "https://vodcdn.test:8080/base", "user_id=1&sign_type=cfl&expired=1786230000&token=" + new string('B', 32))),
            _ => FakePortalTransport.Error("x", r.Path),
        });
        var client = new PortalClient(Options(), transport);

        var vod = await client.ResolveVodAsync("content1", "series1");

        Assert.Equal("MEDIA123", vod.MediaCode);
        Assert.Equal("vodcdn.test:8080", vod.Host);
        Assert.Equal("LIC-vod", vod.License);
        Assert.Equal(new Uri("http://vodcdn.test:8080/vod/MEDIA123_media.mp4"), vod.MediaUri);
        Assert.Contains("sign_type=cfl", vod.CflAuthUrl);

        var play = transport.Requests.Single(r => r.Path == "v10/startPlayVOD").Body;
        Assert.Equal("content1", play["contentId"]!.GetValue<string>());
        Assert.Equal("series1", play["seriesContentId"]!.GetValue<string>());
    }

    [Fact]
    public void Subtitle_files_take_their_format_from_the_url_then_file_type_then_default_to_srt_and_drop_unusable_entries()
    {
        static JsonObject Sub(string lang, string? url, string? fileType = null) => new()
        {
            ["language"] = lang,
            ["file"] = url is null ? new JsonArray() : new JsonArray(new JsonObject { ["url"] = url, ["fileType"] = fileType }),
        };

        var files = PortalClient.SubtitleFiles(new JsonObject
        {
            ["subtitleList"] = new JsonArray(
                Sub("es", "http://vpnwqk.test/public/subs/0824.srt", "vtt"),
                Sub("en", "https://subs.test/get?id=9", "vtt"),
                Sub("pt", "https://subs.test/get?id=10"),
                Sub("fr", null),
                Sub("de", "file:///etc/passwd", "srt"),
                Sub("it", "not a url")),
        });

        Assert.Equal(new[] { "es", "en", "pt" }, files.Select(f => f.Language));
        Assert.Equal(new[] { "srt", "vtt", "srt" }, files.Select(f => f.Format));
        Assert.Equal("http://vpnwqk.test/public/subs/0824.srt", files[0].Url);
    }

    [Fact]
    public void No_subtitle_list_is_no_subtitles()
        => Assert.Empty(PortalClient.SubtitleFiles(new JsonObject()));

    [Fact]
    public async Task ResolveVod_uses_the_ts_extension_when_the_portal_reports_a_ts_video_format()
    {
        // Measured live 2026-09-21: requesting the wrong extension serves a different, invalid file for that
        // title ("moov atom not found" opening a real "ts" title as "_media.mp4").
        var transport = Portal((t, r) => r.Path switch
        {
            "v10/startPlayVOD" => t.Ok(new JsonObject
            {
                ["episodeList"] = new JsonArray(new JsonObject
                {
                    ["totalMovieList"] = new JsonArray(new JsonObject
                    {
                        ["movieList"] = new JsonArray(new JsonObject
                        {
                            ["contentId"] = "MEDIA123",
                            ["videoFormat"] = "ts",
                            ["licenseList"] = new JsonArray(new JsonObject { ["license"] = "LIC-vod" }),
                        }),
                    }),
                }),
            }),
            "v14/getSlbInfo" => Slb(t, Cdn("vod", "https://vodcdn.test:8080/base", "sign_type=cfl&token=" + new string('B', 32))),
            _ => FakePortalTransport.Error("x", r.Path),
        });
        var client = new PortalClient(Options(), transport);

        var vod = await client.ResolveVodAsync("content1");

        Assert.Equal("ts", vod.VideoFormat);
        Assert.Equal(new Uri("http://vodcdn.test:8080/vod/MEDIA123_media.ts"), vod.MediaUri);
    }

    [Fact]
    public async Task ResolveVod_without_media_throws()
    {
        var transport = Portal((t, r) => t.Ok(new JsonObject { ["episodeList"] = new JsonArray() }));
        var client = new PortalClient(Options(), transport);

        await Assert.ThrowsAsync<PortalException>(() => client.ResolveVodAsync("content1"));
    }

    [Theory]
    [InlineData("a=1&expired=1786230000&b=2", 1786230000L)]
    [InlineData("expired=1786230000", 1786230000L)]
    [InlineData("a=1&expired=1786230000000", 1786230000L)]
    public void ParseExpiry_reads_seconds_or_milliseconds(string url, long expectedSeconds)
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expectedSeconds), PortalClient.ParseExpiry(url));
    }

    [Theory]
    [InlineData("a=1&b=2")]
    [InlineData("notexpired=5")]
    [InlineData("")]
    public void ParseExpiry_is_null_when_absent(string url)
    {
        Assert.Null(PortalClient.ParseExpiry(url));
    }
}

public class HttpPortalTransportTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"returnCode\":\"0\"}") };
        }
    }

    [Fact]
    public async Task Posts_the_wire_body_with_the_portal_identity_headers()
    {
        var handler = new CapturingHandler();
        var transport = new HttpPortalTransport(new HttpClient(handler), new PortalOptions
        {
            AppId = "com.example.app",
            ApiBasePath = "/api/core/",
            ApkVer = "1000",
            SpkgVer = "pkg-1",
            PortalUserAgent = "test-ua/1.0",
        });

        var text = await transport.PostAsync("host-a.test", "v8/active", "abcdef", CancellationToken.None);

        Assert.Equal("{\"returnCode\":\"0\"}", text);
        var req = handler.Request!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://host-a.test/api/core/v8/active", req.RequestUri!.ToString());
        Assert.Equal("abcdef", handler.Body);
        Assert.Equal("com.example.app", req.Headers.GetValues("apk").Single());
        Assert.Equal("1000", req.Headers.GetValues("apkVer").Single());
        Assert.Equal("pkg-1", req.Headers.GetValues("spkgVer").Single());
        Assert.Equal("test-ua/1.0", req.Headers.GetValues("User-Agent").Single());
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", req.Content.Headers.ContentType.CharSet);
    }
}
