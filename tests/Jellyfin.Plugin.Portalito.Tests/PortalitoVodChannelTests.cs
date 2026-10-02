using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Channels;
using Jellyfin.Plugin.Portalito.Portal;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public sealed class PortalitoVodChannelTests : IDisposable
{
    private readonly WiringHarness _h = new();
    private readonly FakeVodTrackProbe _probe = new();
    private readonly string _subtitleDir = Path.Combine(Path.GetTempPath(), "portalito-subtitles-test-" + Guid.NewGuid().ToString("N"));
    private readonly Jellyfin.Plugin.Portalito.Collage.CollageService _collages = new(Path.Combine(Path.GetTempPath(), "portalito-collages-test-" + Guid.NewGuid().ToString("N")));
    private readonly FakeUpstream _subtitleHost = new()
    {
        Respond = r => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("1\n00:00:01,000 --> 00:00:02,000\nHola " + r.Uri.AbsolutePath + "\n") },
    };

    private readonly PortalitoVodChannel _channel;

    public PortalitoVodChannelTests()
        => _channel = new PortalitoVodChannel(_h, _probe, new SubtitleFileCache(_subtitleDir, new HttpClient(_subtitleHost)), _collages, NullLogger<PortalitoVodChannel>.Instance);

    public void Dispose()
    {
        foreach (var dir in new[] { _subtitleDir, _collages.Directory })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static JsonObject Content(string id, string name, string programType = "movie") => new()
    {
        ["contentId"] = id,
        ["name"] = name,
        ["programType"] = programType,
    };


    private static InternalChannelItemQuery Query(string? folderId = null, int? start = null, int? limit = null)
        => new() { FolderId = folderId!, StartIndex = start, Limit = limit };

    private static (long E, string S) Params(string url)
    {
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        return (long.Parse(q["e"]!), q["s"]!);
    }

    // ---- root ----

    // A folder's image is the path of its rendered collage file (see CollageService for why a file, not a URL).
    private string Collage(string label, string fit, params string[] images)
        => _collages.PathFor(label, Enum.Parse<Jellyfin.Plugin.Portalito.Collage.CollageFit>(fit), images);

    [Fact]
    public async Task Root_lists_destacado_descubrir_and_tv_en_vivo_each_a_collage_of_what_it_holds()
    {
        var catalogs = CatalogRoutes(_h);
        _h.Route = r => r.Path == "v6/getLiveData"
            ? _h.Ok(new JsonObject { ["channelList"] = WiringHarness.Array(LiveChannelWithLogo("RCN", "https://example.test/rcn.png"), LiveChannelWithLogo("CARACOL", "https://example.test/caracol.png")) })
            : catalogs(r);

        var result = await _channel.GetChannelItems(Query(), default);

        // The portal's own home-screen shelves are not listed (each is hard-capped at 10 items); Destacado leads.
        Assert.Equal(new[] { "fea:root", "dis:root", "liv:root" }, result.Items.Select(i => i.Id));
        Assert.Equal(new[] { "Destacado", "Descubrir", "TV en vivo" }, result.Items.Select(i => i.Name));
        Assert.All(result.Items, i =>
        {
            Assert.Equal(ChannelItemType.Folder, i.Type);
            Assert.Equal(ChannelFolderType.Container, i.FolderType);
        });
        Assert.Equal(3, result.TotalRecordCount);
        // Destacado and Descubrir both draw on the catalogs' titles (the fixture gives every folder the same poster).
        Assert.Equal(Collage("Destacado", "Cover", "https://example.test/thumb.jpg"), result.Items[0].ImageUrl);
        Assert.Equal(Collage("Descubrir", "Cover", "https://example.test/thumb.jpg"), result.Items[1].ImageUrl);
        Assert.Equal(Collage("TV en vivo", "Contain", "https://example.test/rcn.png", "https://example.test/caracol.png"), result.Items[2].ImageUrl);
    }

    [Fact]
    public async Task Root_still_lists_the_three_folders_when_the_portal_is_down_with_name_only_collages()
    {
        var result = await _channel.GetChannelItems(Query(), default);

        Assert.Equal(new[] { "fea:root", "dis:root", "liv:root" }, result.Items.Select(i => i.Id));
        Assert.Equal(Collage("Destacado", "Cover"), result.Items[0].ImageUrl);
        Assert.Equal(Collage("Descubrir", "Cover"), result.Items[1].ImageUrl);
        Assert.Equal(Collage("TV en vivo", "Contain"), result.Items[2].ImageUrl);
    }

    private static JsonObject LiveChannelWithLogo(string code, string logo) => new()
    {
        ["channelCode"] = code,
        ["name"] = code,
        ["posterUrl"] = logo,
    };

    // ---- Destacado (curated rows) ----

    [Fact]
    public async Task Destacado_lists_the_estrenos_and_top_rated_rows()
    {
        _h.Route = CatalogRoutes(_h); // newest vocabulary year is 2020

        var result = await _channel.GetChannelItems(Query("fea:root"), default);

        Assert.Equal(
            new[] { "row:0:estrenos", "row:1:estrenos", "row:3:estrenos", "row:0:top", "row:1:top", "row:3:top" },
            result.Items.Select(i => i.Id));
        Assert.Equal(
            new[] { "Estrenos 2020 · Películas", "Estrenos 2020 · Series", "Anime del momento", "Películas mejor valoradas", "Series mejor valoradas", "Anime mejor valorado" },
            result.Items.Select(i => i.Name));
        Assert.All(result.Items, i => Assert.Equal(ChannelItemType.Folder, i.Type));
    }

    [Fact]
    public async Task An_estrenos_row_lists_the_newest_year()
    {
        var filters = new List<(string Tag, string Year)>();
        var catalogs = CatalogRoutes(_h);
        _h.Route = r =>
        {
            if (r.Path == "v3/filterByContent")
            {
                filters.Add((r.Body["tags"]!.GetValue<string>(), r.Body["year"]!.GetValue<string>()));
            }

            return catalogs(r);
        };

        var result = await _channel.GetChannelItems(Query("row:1:estrenos"), default);

        Assert.Equal("mov:THUMB1", Assert.Single(result.Items).Id);
        Assert.Contains(filters, f => f is { Tag: "", Year: "2020" });
    }

    [Fact]
    public async Task A_top_rated_row_lists_titles_ranked_by_score()
    {
        static JsonObject Rated(string id, double score) => new() { ["contentId"] = id, ["name"] = id, ["score"] = score };
        var catalogs = CatalogRoutes(_h);
        _h.Route = r => r.Path != "v3/filterByContent"
            ? catalogs(r)
            : r.Body["pageNum"]!.GetValue<int>() == 1
                ? _h.Ok(new JsonObject { ["assetList"] = WiringHarness.Array(Rated("A", 6.0), Rated("B", 9.0), Rated("C", 7.5)) })
                : _h.Ok(new JsonObject { ["assetList"] = WiringHarness.Array() });

        var result = await _channel.GetChannelItems(Query("row:0:top"), default);

        Assert.Equal(new[] { "mov:B", "mov:C", "mov:A" }, result.Items.Select(i => i.Id));
    }

    [Theory]
    [InlineData("col:111")]
    [InlineData("ser:SER1")]
    public async Task Ids_from_the_removed_native_shelf_browsing_are_rejected(string folderId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _channel.GetChannelItems(Query(folderId), default));
        Assert.Empty(_h.Transport.Requests);
    }

    // ---- "TV en vivo" ----

    private static Func<FakeRequest, string> LiveRoute(WiringHarness h, params JsonObject[] channels)
        => r => r.Path == "v6/getLiveData"
            ? h.Ok(new JsonObject { ["channelList"] = new JsonArray(channels.Cast<JsonNode?>().ToArray()) })
            : FakePortalTransport.Error("x", r.Path);

    [Fact]
    public async Task Without_the_portals_category_list_tv_en_vivo_falls_back_to_every_channel_as_playable_live_items()
    {
        // LiveRoute answers only v6/getLiveData, so the category list (getNextColumns) fails: the fallback path.
        _h.Route = LiveRoute(_h,
            new JsonObject { ["channelCode"] = "cx_A1_720p", ["name"] = "Canal Uno", ["posterUrl"] = "https://example.test/uno.png" },
            new JsonObject { ["channelCode"] = "B2", ["name"] = "" },
            new JsonObject { ["channelCode"] = "../evil", ["name"] = "Bad" },
            new JsonObject { ["name"] = "No code" });

        var result = await _channel.GetChannelItems(Query("liv:root"), default);

        // Unsafe and missing codes are dropped; a blank name falls back to the code.
        Assert.Equal(new[] { "lch:cx_A1_720p", "lch:B2" }, result.Items.Select(i => i.Id));
        Assert.Equal(new[] { "Canal Uno", "B2" }, result.Items.Select(i => i.Name));
        Assert.All(result.Items, i =>
        {
            Assert.Equal(ChannelItemType.Media, i.Type);
            Assert.Equal(ChannelMediaType.Video, i.MediaType);
            Assert.True(i.IsLiveStream);
        });
        Assert.Equal(_h.Signer.ImageUrl("https://example.test/uno.png", MediaSources.ImageUrlValidity), result.Items[0].ImageUrl);
        Assert.Null(result.Items[1].ImageUrl);
        Assert.Equal(999L, _h.Transport.Requests.Single(r => r.Path == "v6/getLiveData").Body["columnId"]!.GetValue<long>());
    }

    /// <summary>The live category list, plus each category's channels: one whose logo is named after the columnId.</summary>
    private static Func<FakeRequest, string> LiveCategoriesRoute(WiringHarness h, params JsonObject[] categories)
        => r => r.Path switch
        {
            "getNextColumns" => h.Ok(new JsonObject { ["recommendList"] = new JsonArray(categories.Cast<JsonNode?>().ToArray()) }),
            "v6/getLiveData" => h.Ok(new JsonObject { ["channelList"] = WiringHarness.Array(LiveChannelWithLogo("C" + r.Body["columnId"], $"https://example.test/{r.Body["columnId"]}.png")) }),
            _ => FakePortalTransport.Error("x", r.Path),
        };

    [Fact]
    public async Task Tv_en_vivo_lists_the_portals_live_categories_in_its_order_with_todos_first()
    {
        _h.Route = LiveCategoriesRoute(_h,
            new JsonObject { ["columnId"] = 76301, ["name"] = "Colombia", ["posterList"] = WiringHarness.Array(new JsonObject { ["fileType"] = "icon", ["fileUrl"] = "https://example.test/co.png" }) },
            new JsonObject { ["columnId"] = "76206", ["name"] = "Deportes" },
            new JsonObject { ["columnId"] = "not-a-number", ["name"] = "Bad" },
            new JsonObject { ["columnId"] = 77777 });

        var result = await _channel.GetChannelItems(Query("liv:root"), default);

        // Mirrors the portal (getNextColumns(liveColumnCode)): its order, its names.
        Assert.Equal(new[] { "lcat:999", "lcat:76301", "lcat:76206" }, result.Items.Select(i => i.Id));
        Assert.Equal(new[] { "Todos los canales", "Colombia", "Deportes" }, result.Items.Select(i => i.Name));
        Assert.All(result.Items, i => Assert.Equal(ChannelItemType.Folder, i.Type));
        // Each category's thumbnail: a collage of its own channels' logos with its name.
        Assert.Equal(Collage("Todos los canales", "Contain", "https://example.test/999.png"), result.Items[0].ImageUrl);
        Assert.Equal(Collage("Colombia", "Contain", "https://example.test/76301.png"), result.Items[1].ImageUrl);
        Assert.Equal(Collage("Deportes", "Contain", "https://example.test/76206.png"), result.Items[2].ImageUrl);
        Assert.Equal("live_all", _h.Transport.Requests.Single(r => r.Path == "getNextColumns").Body["columnCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task When_the_portal_lists_every_channel_itself_todos_is_not_added_twice()
    {
        _h.Route = LiveCategoriesRoute(_h,
            new JsonObject { ["columnId"] = 76301, ["name"] = "Colombia" },
            new JsonObject { ["columnId"] = 999, ["name"] = "ChannelList" });

        var result = await _channel.GetChannelItems(Query("liv:root"), default);

        Assert.Equal(new[] { "lcat:76301", "lcat:999" }, result.Items.Select(i => i.Id));
        // Its own name for it ("ChannelList") isn't Spanish or meant for viewers.
        Assert.Equal(new[] { "Colombia", "Todos los canales" }, result.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_live_category_lists_its_own_channels()
    {
        _h.Route = LiveRoute(_h, new JsonObject { ["channelCode"] = "cx-EXAMPLE", ["name"] = "RCN" });

        var result = await _channel.GetChannelItems(Query("lcat:76301"), default);

        Assert.Equal(new[] { "lch:cx-EXAMPLE" }, result.Items.Select(i => i.Id));
        Assert.Equal(76301, _h.Transport.Requests.Single(r => r.Path == "v6/getLiveData").Body["columnId"]!.GetValue<long>());
    }

    // ---- series ----

    [Fact]
    public async Task Season_folder_lists_episodes_with_their_numbers()
    {
        _h.Route = r => r.Path == "v4/getItemData"
            ? _h.Ok(new JsonObject
            {
                ["assetData"] = new JsonObject
                {
                    ["simpleProgramList"] = WiringHarness.Array(
                        new JsonObject
                        {
                            ["contentId"] = "EP1",
                            ["name"] = "Pilot",
                            ["seriesNumber"] = "1",
                            ["posterList"] = WiringHarness.Array(new JsonObject { ["fileType"] = "icon", ["fileUrl"] = "https://example.test/ep1.jpg" }),
                        },
                        new JsonObject { ["contentId"] = "EP2", ["name"] = "Second", ["seriesNumber"] = 2 },
                        new JsonObject { ["contentId"] = "EP3" }),
                },
            })
            : FakePortalTransport.Error("x", r.Path);

        var result = await _channel.GetChannelItems(Query("ssn:SER1"), default);

        Assert.Equal(new[] { "epi:SER1:EP1", "epi:SER1:EP2", "epi:SER1:EP3" }, result.Items.Select(i => i.Id));
        Assert.Equal(new int?[] { 1, 2, null }, result.Items.Select(i => i.IndexNumber));
        Assert.Equal("EP3", result.Items[2].Name);
        // Routed through the image proxy (not hotlinked) -- the CDN's own "image/jpg" Content-Type 400s in
        // Jellyfin's image cache/converter otherwise (see ProxyUrlSigner.ImageUrl).
        Assert.Equal(_h.Signer.ImageUrl("https://example.test/ep1.jpg", MediaSources.ImageUrlValidity), result.Items[0].ImageUrl);
        Assert.Null(result.Items[1].ImageUrl);
        Assert.All(result.Items, i =>
        {
            Assert.Equal(ChannelItemType.Media, i.Type);
            Assert.Equal(ChannelMediaContentType.Episode, i.ContentType);
        });
        Assert.Equal(3, result.TotalRecordCount);

        var body = _h.Transport.Requests.Single(r => r.Path == "v4/getItemData").Body;
        Assert.Equal("SER1", body["contentId"]!.GetValue<string>());
        Assert.Equal("0", body["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Season_episodes_are_sliced_by_the_window()
    {
        _h.Route = _ => _h.Ok(new JsonObject
        {
            ["assetData"] = new JsonObject
            {
                ["simpleProgramList"] = new JsonArray(Enumerable.Range(1, 10)
                    .Select(i => (JsonNode?)new JsonObject { ["contentId"] = $"EP{i}", ["name"] = $"E{i}" })
                    .ToArray()),
            },
        });

        var result = await _channel.GetChannelItems(Query("ssn:S", 3, 2), default);

        Assert.Equal(new[] { "epi:S:EP4", "epi:S:EP5" }, result.Items.Select(i => i.Id));
        Assert.Equal(10, result.TotalRecordCount);
    }

    // ---- bad input & failures ----

    [Theory]
    [InlineData("garbage")]
    [InlineData("mov:M1")]
    [InlineData("epi:S:E")]
    public async Task Unusable_folder_ids_are_rejected(string folderId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _channel.GetChannelItems(Query(folderId), default));
        Assert.Empty(_h.Transport.Requests);
    }

    [Fact]
    public async Task A_portal_failure_surfaces_as_a_portal_exception()
    {
        _h.Route = _ => FakePortalTransport.Error("bad", "boom");

        await Assert.ThrowsAsync<PortalException>(() => _channel.GetChannelItems(Query("ssn:1"), default));
    }

    // ---- playback ----
    //
    // GetChannelItemMediaInfo resolves through the portal (unlike browsing) to learn the real CDN container --
    // "ts" or "mp4", from the portal's own videoFormat -- before handing Jellyfin a MediaSourceInfo. Requesting the
    // wrong one serves a different, invalid file for that title (measured live 2026-09-21: ffmpeg got "moov atom
    // not found" opening a real "ts" title as "_media.mp4").

    internal static Func<FakeRequest, string> VodRoute(WiringHarness h, string? videoFormat = null, JsonArray? subtitleList = null)
    {
        var movie = new JsonObject
        {
            ["contentId"] = "IGNORED",
            ["licenseList"] = WiringHarness.Array(new JsonObject { ["license"] = "LIC" }),
        };
        if (videoFormat is not null)
        {
            movie["videoFormat"] = videoFormat;
        }

        return r => r.Path switch
        {
            "v10/startPlayVOD" => h.Ok(new JsonObject
            {
                ["episodeList"] = WiringHarness.Array(new JsonObject
                {
                    // Cloned per response: a node can't have two parents, and a route may answer several times.
                    ["totalMovieList"] = WiringHarness.Array(new JsonObject { ["movieList"] = WiringHarness.Array(movie.DeepClone()) }),
                    ["subtitleList"] = subtitleList?.DeepClone() ?? new JsonArray(),
                }),
            }),
            "v14/getSlbInfo" => h.Ok(new JsonObject
            {
                ["cdn_list"] = WiringHarness.Array(new JsonObject
                {
                    ["tag"] = "vod",
                    ["main_addr"] = "http://cdn.test",
                    ["url_list"] = WiringHarness.Array(new JsonObject { ["url"] = "sign_type=cfl&token=" + new string('A', 32) }),
                }),
            }),
            _ => FakePortalTransport.Error("x", r.Path),
        };
    }

    [Fact]
    public async Task A_movie_resolves_to_a_signed_vod_proxy_url_with_the_portal_reported_container()
    {
        _h.Route = VodRoute(_h);

        var sources = (await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default)).ToList();

        var source = Assert.Single(sources);
        Assert.StartsWith(WiringHarness.ProxyBase + "/Portalito/vod/MOVIE1?e=", source.Path);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.True(source.IsRemote);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.IsInfiniteStream);
        Assert.Equal("mp4", source.Container);
        // A GUID derived from the item id, stable across calls: Jellyfin's HLS master playlist Guid.Parse()s it
        // for trickplay once a source has a runtime, and the web player's playback 500'd on "mov:..." (2026-09-24).
        Assert.Equal(MediaSources.SourceIdFor("mov:MOVIE1"), source.Id);
        Assert.True(Guid.TryParse(source.Id, out _));
        Assert.Equal(MediaSources.SourceIdFor("mov:MOVIE1"), MediaSources.SourceIdFor("mov:MOVIE1"));
        Assert.NotEqual(source.Id, MediaSources.SourceIdFor("mov:MOVIE2"));

        var (e, s) = Params(source.Path);
        Assert.True(_h.Signer.VerifyVod("MOVIE1", string.Empty, e, s));
        Assert.Equal(1, _h.Transport.Count("v10/startPlayVOD"));
    }

    [Fact]
    public async Task Container_is_ts_when_the_portal_reports_a_ts_video_format()
    {
        _h.Route = VodRoute(_h, "ts");

        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default));

        Assert.Equal("ts", source.Container);
    }

    [Fact]
    public async Task A_portal_failure_resolving_playback_surfaces_as_a_portal_exception()
    {
        _h.Route = r => FakePortalTransport.Error("bad", "boom");

        await Assert.ThrowsAsync<PortalException>(() => _channel.GetChannelItemMediaInfo("mov:MOVIE1", default));
    }

    [Fact]
    public async Task An_episode_resolves_to_the_episode_id_with_its_series_bound_into_the_signature()
    {
        _h.Route = VodRoute(_h);

        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("epi:SER1:EP2", default));

        Assert.StartsWith(WiringHarness.ProxyBase + "/Portalito/vod/EP2?series=SER1&e=", source.Path);
        var (e, s) = Params(source.Path);
        Assert.True(_h.Signer.VerifyVod("EP2", "SER1", e, s));
        Assert.False(_h.Signer.VerifyVod("EP2", string.Empty, e, s));
        Assert.False(_h.Signer.VerifyVod("EP2", "OTHER", e, s));

        // Resolved via the episode+series id, not the movie-shaped call.
        var play = _h.Transport.Requests.Single(r => r.Path == "v10/startPlayVOD").Body;
        Assert.Equal("EP2", play["contentId"]!.GetValue<string>());
        Assert.Equal("SER1", play["seriesContentId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Vod_url_stays_valid_across_a_long_film()
    {
        _h.Route = VodRoute(_h);

        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("mov:M1", default));
        var (e, s) = Params(source.Path);

        _h.Clock.Now += TimeSpan.FromHours(11);
        Assert.True(_h.Signer.VerifyVod("M1", string.Empty, e, s));
    }

    [Fact]
    public async Task A_live_channel_resolves_to_the_signed_live_playlist_with_no_portal_call()
    {
        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("lch:cx_A1_720p", default));

        // The same source Jellyfin's Live TV section plays, keyed by this channel item's own id.
        Assert.StartsWith(WiringHarness.ProxyBase + "/Portalito/live/cx_A1_720p.m3u8?e=", source.Path);
        Assert.True(source.IsInfiniteStream);
        Assert.Equal(MediaSources.SourceIdFor("lch:cx_A1_720p"), source.Id);
        var (e, s) = Params(source.Path);
        Assert.True(_h.Signer.VerifyLivePlaylist("cx_A1_720p", e, s));
        Assert.Empty(_h.Transport.Requests);
    }

    [Theory]
    [InlineData("ssn:S1")]
    [InlineData("shw:S1")]
    [InlineData("liv:root")]
    [InlineData("garbage")]
    [InlineData("")]
    public async Task Only_movies_episodes_and_live_channels_are_playable(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _channel.GetChannelItemMediaInfo(id, default));
    }

    // ---- audio tracks + subtitles ----
    //
    // Jellyfin does not probe sources a channel returns from IRequiresMediaInfoCallback, so without these the player
    // had no audio picker and ffmpeg took its default track -- and Portalito files carry several language-tagged audio
    // tracks (ffprobe, 2026-09-23: por/spa/spa/eng on one title). Subtitles are separate files play_vod lists.

    private static MediaStream Stream(MediaStreamType type, int index, string? language = null) => new() { Type = type, Index = index, Language = language };

    // The shape measured on a real title: one video track, then por/spa/spa/eng audio.
    private static Channels.ProbedTracks FiveTracks() => new(
        new[]
        {
            Stream(MediaStreamType.Video, 0),
            Stream(MediaStreamType.Audio, 1, "por"),
            Stream(MediaStreamType.Audio, 2, "spa"),
            Stream(MediaStreamType.Audio, 3, "spa"),
            Stream(MediaStreamType.Audio, 4, "eng"),
        },
        RunTimeTicks: 123_000_000,
        Bitrate: 5_000_000);

    private static JsonArray Subs(params (string Language, string? Url, string? FileType)[] subs)
        => new(subs.Select(x => (JsonNode?)new JsonObject
        {
            ["language"] = x.Language,
            ["file"] = x.Url is null ? new JsonArray() : new JsonArray(new JsonObject { ["url"] = x.Url, ["fileType"] = x.FileType }),
        }).ToArray());

    [Fact]
    public async Task A_title_offers_its_probed_audio_tracks_with_spanish_default_plus_its_subtitle_files()
    {
        _probe.Respond = () => FiveTracks();
        _h.Route = VodRoute(_h, "ts", Subs(("es", "https://subs.test/a/es.srt", "srt"), ("en", "https://subs.test/a/en.vtt", null), ("fr", null, null)));

        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default));

        // The probe ran against the same signed proxy URL ffmpeg will play.
        Assert.Equal(source.Path, _probe.LastSource!.Path);
        Assert.Equal(123_000_000, source.RunTimeTicks);
        Assert.Equal(5_000_000, source.Bitrate);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, source.MediaStreams.Select(m => m.Index));
        // First Spanish audio is the default for users with no preference of their own; nothing else is.
        Assert.Equal(new[] { 2 }, source.MediaStreams.Where(m => m.Type == MediaStreamType.Audio && m.IsDefault).Select(m => m.Index));

        // The file with no url ("fr") is dropped: the portal sometimes lists languages never uploaded.
        var subtitles = source.MediaStreams.Where(m => m.Type == MediaStreamType.Subtitle).ToList();
        Assert.Equal(new[] { "spa", "eng" }, subtitles.Select(m => m.Language));
        Assert.Equal(new[] { "srt", "vtt" }, subtitles.Select(m => m.Codec));
        Assert.All(subtitles, m =>
        {
            Assert.True(m.IsExternal);
            Assert.True(m.SupportsExternalStream);
        });

        // Served from local copies, not the portal URLs: Jellyfin 10.11.11's SubtitleEncoder closes an http
        // subtitle's stream before reading it ("Cannot access a closed Stream", measured live).
        Assert.All(subtitles, m => Assert.StartsWith(_subtitleDir, m.Path));
        Assert.EndsWith(".srt", subtitles[0].Path);
        Assert.EndsWith(".vtt", subtitles[1].Path);
        Assert.Contains("/a/es.srt", File.ReadAllText(subtitles[0].Path));
        Assert.Equal(new[] { "/a/es.srt", "/a/en.vtt" }, _subtitleHost.Requests.Select(r => r.Uri.AbsolutePath).Order().Reverse());
    }

    [Fact]
    public async Task Subtitle_files_are_downloaded_once_and_one_that_fails_is_left_out()
    {
        _subtitleHost.Respond = r => r.Uri.AbsolutePath.Contains("missing", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("WEBVTT\n") };
        _h.Route = VodRoute(_h, subtitleList: Subs(("es", "https://subs.test/es.vtt", "vtt"), ("en", "https://subs.test/missing.vtt", "vtt")));

        var first = Assert.Single(await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default));
        await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default);

        Assert.Equal(new[] { "spa" }, first.MediaStreams.Where(m => m.Type == MediaStreamType.Subtitle).Select(m => m.Language));
        // The good file is fetched once and reused; the missing one is retried, since nothing was stored for it.
        Assert.Equal(1, _subtitleHost.Requests.Count(r => r.Uri.AbsolutePath == "/es.vtt"));
        Assert.Equal(2, _subtitleHost.Requests.Count(r => r.Uri.AbsolutePath == "/missing.vtt"));
    }

    [Fact]
    public async Task Without_a_spanish_track_the_probed_defaults_are_left_alone()
    {
        _probe.Respond = () => new Channels.ProbedTracks(
            new[] { Stream(MediaStreamType.Video, 0), Stream(MediaStreamType.Audio, 1, "eng"), new MediaStream { Type = MediaStreamType.Audio, Index = 2, Language = "jpn", IsDefault = true } },
            null,
            null);
        _h.Route = VodRoute(_h);

        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default));

        Assert.Equal(new[] { 2 }, source.MediaStreams.Where(m => m.IsDefault).Select(m => m.Index));
    }

    [Fact]
    public async Task A_title_is_probed_once_then_served_from_cache()
    {
        _probe.Respond = () => FiveTracks();
        _h.Route = VodRoute(_h);

        await _channel.GetChannelItemMediaInfo("epi:SER1:EP1", default);
        var second = Assert.Single(await _channel.GetChannelItemMediaInfo("epi:SER1:EP1", default));

        Assert.Equal(1, _probe.Calls);
        Assert.Equal(5, second.MediaStreams.Count);
    }

    [Fact]
    public async Task A_failed_probe_still_plays_offers_the_subtitles_and_is_retried_next_time()
    {
        _probe.Respond = () => throw new InvalidOperationException("ffprobe exited 1");
        _h.Route = VodRoute(_h, subtitleList: Subs(("es", "https://subs.test/es.srt", "srt")));

        var source = Assert.Single(await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default));

        Assert.StartsWith(WiringHarness.ProxyBase + "/Portalito/vod/MOVIE1?", source.Path);
        var only = Assert.Single(source.MediaStreams);
        Assert.Equal(MediaStreamType.Subtitle, only.Type);

        await _channel.GetChannelItemMediaInfo("mov:MOVIE1", default);
        Assert.Equal(2, _probe.Calls);
    }

    [Fact]
    public async Task Live_channels_are_not_probed()
    {
        await _channel.GetChannelItemMediaInfo("lch:cx_A1_720p", default);

        Assert.Equal(0, _probe.Calls);
    }

    // ---- TMDB fallback (fills only what Portalito leaves blank) ----

    [Fact]
    public async Task With_a_tmdb_key_blank_synopses_and_missing_posters_are_filled_and_portal_fields_are_kept()
    {
        var h = new WiringHarness(withTmdb: true);
        var channel = new PortalitoVodChannel(h, new FakeVodTrackProbe(), new SubtitleFileCache(_subtitleDir, new HttpClient(_subtitleHost)), _collages, NullLogger<PortalitoVodChannel>.Instance);
        var blank = Content("M1", "Batman: El Caballero de La Noche");
        blank["alias"] = "The Dark Knight";
        blank["releaseTime"] = "2008-08-13";
        var described = Content("M2", "Con sinopsis");
        described["description"] = "La de Portalito.";
        described["releaseTime"] = "2010-01-01";
        described["posterList"] = WiringHarness.Array(new JsonObject { ["fileType"] = "icon", ["fileUrl"] = "https://example.test/portal.jpg" });
        var listing = WiringHarness.Array(blank, described);
        h.Route = r => r.Path == "v3/filterByContent" ? h.Ok(new JsonObject { ["assetList"] = listing.DeepClone() }) : CatalogRoutes(h)(r);
        h.Tmdb.Results = (_, q) => new JsonArray(new JsonObject
        {
            ["title"] = q,
            ["original_title"] = q,
            ["release_date"] = q == "The Dark Knight" ? "2008-07-16" : "2010-01-01",
            ["overview"] = "Sinopsis de TMDB para " + q,
            ["poster_path"] = "/tmdb.jpg",
        });

        var result = await channel.GetChannelItems(Query("flt:0:all"), default);

        var byId = result.Items.ToDictionary(i => i.Id);
        Assert.Equal("Sinopsis de TMDB para The Dark Knight", byId["mov:M1"].Overview);
        Assert.Equal("https://image.tmdb.org/t/p/w342/tmdb.jpg", byId["mov:M1"].ImageUrl);
        // Portalito's own synopsis and poster win; TMDB is never even asked for a title that has both.
        Assert.Equal("La de Portalito.", byId["mov:M2"].Overview);
        Assert.Equal(h.Signer.ImageUrl("https://example.test/portal.jpg", MediaSources.ImageUrlValidity), byId["mov:M2"].ImageUrl);
        Assert.DoesNotContain(h.Tmdb.Requests, r => r.Query["query"] == "Con sinopsis");
    }

    [Fact]
    public async Task Without_a_tmdb_key_nothing_is_looked_up()
    {
        _h.Route = r => r.Path == "v3/filterByContent" ? _h.Ok(new JsonObject { ["assetList"] = WiringHarness.Array(Content("M1", "Sin sinopsis")) }) : CatalogRoutes(_h)(r);

        var result = await _channel.GetChannelItems(Query("flt:0:all"), default);

        Assert.Null(Assert.Single(result.Items).Overview);
        Assert.Empty(_h.Tmdb.Requests);
    }

    // ---- channel metadata ----

    [Fact]
    public void Features_declare_video_movies_and_episodes_and_the_portal_page_size_as_the_page_limit()
    {
        var features = _channel.GetChannelFeatures();

        Assert.Equal(new[] { ChannelMediaType.Video }, features.MediaTypes);
        Assert.Contains(ChannelMediaContentType.Movie, features.ContentTypes);
        Assert.Contains(ChannelMediaContentType.Episode, features.ContentTypes);
        Assert.Equal(30, features.MaxPageSize);
    }

    [Fact]
    public void The_channel_is_enabled_only_once_the_plugin_is_configured()
    {
        Assert.True(_channel.IsEnabledFor("user"));
        Assert.False(new PortalitoVodChannel(new UnconfiguredProvider(), new FakeVodTrackProbe(), new SubtitleFileCache(_subtitleDir, new HttpClient(_subtitleHost)), _collages, NullLogger<PortalitoVodChannel>.Instance).IsEnabledFor("user"));
    }

    [Fact]
    public async Task A_series_with_more_than_one_page_of_episodes_returns_all_of_them_when_no_limit_is_given()
    {
        var episodes = Enumerable.Range(1, 45)
            .Select(n => (JsonNode)new JsonObject { ["contentId"] = $"EP{n}", ["name"] = $"Ep {n}", ["seriesNumber"] = n })
            .ToArray();
        _h.Route = r => r.Path == "v4/getItemData"
            ? _h.Ok(new JsonObject { ["assetData"] = new JsonObject { ["simpleProgramList"] = WiringHarness.Array(episodes) } })
            : FakePortalTransport.Error("x", r.Path);

        var result = await _channel.GetChannelItems(Query("ssn:SER1"), default);

        Assert.Equal(45, result.Items.Count);
    }

    [Fact]
    public async Task Catalog_movies_and_shows_carry_their_icon_poster_as_the_image_url()
    {
        JsonObject WithPoster(JsonObject content, string iconUrl)
        {
            content["posterList"] = WiringHarness.Array(
                new JsonObject { ["fileType"] = "stage", ["fileUrl"] = "https://example.test/stage.jpg" },
                new JsonObject { ["fileType"] = "icon", ["fileUrl"] = iconUrl },
                new JsonObject { ["fileType"] = "poster", ["fileUrl"] = "https://example.test/backdrop.jpg" });
            return content;
        }

        var listing = WiringHarness.Array(
            WithPoster(Content("MOVIE1", "A Movie"), "https://example.test/movie-poster.jpg"),
            WithPoster(Content("SER1", "A Show", "series"), "https://example.test/series-poster.jpg"),
            Content("MOVIE2", "No Poster"));
        _h.Route = r => r.Path == "v3/filterByContent"
            ? _h.Ok(new JsonObject { ["assetList"] = listing })
            : CatalogRoutes(_h)(r);

        var result = await _channel.GetChannelItems(Query("flt:0:all"), default);

        var byId = result.Items.ToDictionary(i => i.Id);
        // The "icon" entry is used (a browse-grid portrait poster), not "stage" (a 100x100 thumbnail) or "poster"
        // (ChannelItemInfo.ImageUrl has no separate backdrop slot) -- and routed through the image proxy, not
        // hotlinked (see ProxyUrlSigner.ImageUrl).
        Assert.Equal(_h.Signer.ImageUrl("https://example.test/movie-poster.jpg", MediaSources.ImageUrlValidity), byId["mov:MOVIE1"].ImageUrl);
        Assert.Equal(_h.Signer.ImageUrl("https://example.test/series-poster.jpg", MediaSources.ImageUrlValidity), byId["shw:" + ShowIndex.KeyFor("A Show")].ImageUrl);
        Assert.Null(byId["mov:MOVIE2"].ImageUrl);
    }

    // ---- "Descubrir": Portalito's own full catalog (2026-09-23) ----
    //
    // Replaces the earlier TMDB/AniList-driven picker: v3/filterByContent is a real paginated catalog (unlike the
    // 10-item shelf carousels), but its columnId is a root's parentId, not any shelf's own columnId -- derived via
    // getNextColumns + getRecommendColumnContents, not hardcoded. See CatalogBrowser and PROGRESS.md.

    /// <summary>Routes getNextColumns/getRecommendColumnContents/v3/filterGenre/v3/filterByContent -- everything
    /// CatalogBrowser needs, including the small listings folder collages are drawn
    /// from -- except a listing/detail call a specific test wants to assert on itself.</summary>
    private static Func<FakeRequest, string> CatalogRoutes(WiringHarness h, long parentId = 76177, string[]? tags = null, int[]? years = null)
    {
        tags ??= new[] { "Action", "Comedy" };
        years ??= new[] { 2020, 2019 };
        return r => r.Path switch
        {
            "getNextColumns" => h.Ok(new JsonObject { ["recommendList"] = WiringHarness.Array(new JsonObject { ["columnId"] = 999 }) }),
            "getRecommendColumnContents" => h.Ok(new JsonObject { ["columnList"] = WiringHarness.Array(new JsonObject { ["parentId"] = parentId }) }),
            "v3/filterGenre" => h.Ok(new JsonObject
            {
                ["tags"] = new JsonArray(tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["year"] = new JsonArray(years.Select(y => (JsonNode?)JsonValue.Create(y.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray()),
            }),
            "v3/filterByContent" => h.Ok(new JsonObject
            {
                ["assetList"] = WiringHarness.Array(new JsonObject
                {
                    ["contentId"] = "THUMB1",
                    ["name"] = "Thumb Item",
                    ["tags"] = tags[0],
                    ["releaseTime"] = $"{years[0]}-01-01",
                    ["posterList"] = WiringHarness.Array(new JsonObject { ["fileType"] = "icon", ["fileUrl"] = "https://example.test/thumb.jpg" }),
                }),
            }),
            _ => FakePortalTransport.Error("x", r.Path),
        };
    }

    [Fact]
    public async Task Catalog_collages_are_picked_specific_first_so_siblings_and_todo_dont_repeat()
    {
        // Each genre's newest titles, plus the catalog's own. "Action" shares everything with the catalog; "Comedy"
        // has titles of its own. The catalog-wide list holds all of them.
        string P(string n) => $"https://example.test/{n}.jpg";
        var byTag = new Dictionary<string, string[]>
        {
            ["Action"] = new[] { "n1", "n2", "n3", "n4", "c1" },
            ["Comedy"] = new[] { "c1", "c2", "c3", "c4" },
            [""] = new[] { "n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9", "n10", "n11", "n12", "c1" },
        };
        var catalog = CatalogRoutes(_h, tags: new[] { "Action", "Comedy" });
        _h.Route = r => r.Path == "v3/filterByContent"
            ? _h.Ok(new JsonObject
            {
                ["assetList"] = new JsonArray(byTag[r.Body["tags"]!.GetValue<string>()].Select(n => (JsonNode?)new JsonObject
                {
                    ["contentId"] = n.ToUpperInvariant(),
                    // Letters only: a collage key ignores digits (so "Toy Story 4"/"5" count as one show).
                    ["name"] = string.Concat(n.Select(ch => char.IsDigit(ch) ? (char)('a' + (ch - '0')) : ch)),
                    ["posterList"] = WiringHarness.Array(new JsonObject { ["fileType"] = "icon", ["fileUrl"] = P(n) }),
                }).ToArray()),
            })
            : catalog(r);

        var byId = (await _channel.GetChannelItems(Query("cat:0"), default)).Items.ToDictionary(i => i.Id);

        // Comedy (its titles are its own) picks first and keeps them; Action then takes the newest ones; Todo and
        // Por año get the next newest instead of repeating Action's.
        Assert.Equal(Collage("Comedia", "Cover", P("c1"), P("c2"), P("c3"), P("c4")), byId["flt:0:g1"].ImageUrl);
        Assert.Equal(Collage("Acción", "Cover", P("n1"), P("n2"), P("n3"), P("n4")), byId["flt:0:g0"].ImageUrl);
        Assert.Equal(Collage("Todo", "Cover", P("n5"), P("n6"), P("n7"), P("n8")), byId["flt:0:all"].ImageUrl);
        Assert.Equal(Collage("Por año", "Cover", P("n9"), P("n10"), P("n11"), P("n12")), byId["flt:0:years"].ImageUrl);
    }

    [Fact]
    public async Task Discover_root_lists_the_four_catalogs_each_a_collage_of_its_own_newest_titles()
    {
        _h.Route = CatalogRoutes(_h);

        var result = await _channel.GetChannelItems(Query("dis:root"), default);

        Assert.Equal(new[] { "cat:0", "cat:1", "cat:2", "cat:3" }, result.Items.Select(i => i.Id));
        Assert.Equal(new[] { "Películas", "Series", "Infantil", "Anime" }, result.Items.Select(i => i.Name));
        Assert.Equal(4, result.TotalRecordCount);
        Assert.Equal(Collage("Películas", "Cover", "https://example.test/thumb.jpg"), result.Items[0].ImageUrl);
        Assert.Equal(Collage("Anime", "Cover", "https://example.test/thumb.jpg"), result.Items[3].ImageUrl);
    }

    [Fact]
    public async Task Catalog_folder_lists_todo_then_each_genre_then_por_ano()
    {
        _h.Route = CatalogRoutes(_h, tags: new[] { "Action", "Comedy" });

        var result = await _channel.GetChannelItems(Query("cat:0"), default);

        Assert.Equal(new[] { "flt:0:all", "flt:0:g0", "flt:0:g1", "flt:0:years" }, result.Items.Select(i => i.Id));
        Assert.Equal(new[] { "Todo", "Acción", "Comedia", "Por año" }, result.Items.Select(i => i.Name));

        const string poster = "https://example.test/thumb.jpg";
        var byId = result.Items.ToDictionary(i => i.Id);
        Assert.Equal(Collage("Todo", "Cover", poster), byId["flt:0:all"].ImageUrl);
        Assert.Equal(Collage("Acción", "Cover", poster), byId["flt:0:g0"].ImageUrl);
        Assert.Equal(Collage("Comedia", "Cover", poster), byId["flt:0:g1"].ImageUrl);
        Assert.Equal(Collage("Por año", "Cover", poster), byId["flt:0:years"].ImageUrl);

        // Each genre's collage comes from its own filtered listing.
        var tagsAsked = _h.Transport.Requests.Where(r => r.Path == "v3/filterByContent").Select(r => r.Body["tags"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("Action", tagsAsked);
        Assert.Contains("Comedy", tagsAsked);
    }

    [Fact]
    public async Task Filter_all_sends_the_resolved_parent_id_with_no_tag_or_year()
    {
        JsonObject? sent = null;
        _h.Route = r =>
        {
            if (r.Path == "v3/filterByContent")
            {
                sent = r.Body;
                return _h.Ok(new JsonObject { ["assetList"] = new JsonArray() });
            }

            return CatalogRoutes(_h)(r);
        };

        await _channel.GetChannelItems(Query("flt:0:all"), default);

        Assert.Equal(76177, sent!["columnId"]!.GetValue<long>());
        Assert.Equal(string.Empty, sent["tags"]!.GetValue<string>());
        Assert.Equal(string.Empty, sent["year"]!.GetValue<string>());
        Assert.Equal(CatalogBrowser.ListingSize, sent["pageSize"]!.GetValue<int>());
    }

    [Fact]
    public async Task Filter_genre_sends_the_real_english_tag_name_not_its_spanish_label_or_index()
    {
        string? tagSent = null;
        _h.Route = r =>
        {
            if (r.Path == "v3/filterByContent")
            {
                tagSent = r.Body["tags"]!.GetValue<string>();
                return _h.Ok(new JsonObject { ["assetList"] = new JsonArray() });
            }

            return CatalogRoutes(_h, tags: new[] { "Action", "Comedy" })(r);
        };

        // Index 1 is "Comedy" ("Comedia" on screen) -- the folder id carries the index, the tag lookup happens on open.
        await _channel.GetChannelItems(Query("flt:0:g1"), default);

        Assert.Equal("Comedy", tagSent);
    }

    [Fact]
    public async Task Filter_year_sends_the_year_and_no_tag()
    {
        JsonObject? sent = null;
        _h.Route = r =>
        {
            if (r.Path == "v3/filterByContent")
            {
                sent = r.Body;
                return _h.Ok(new JsonObject { ["assetList"] = new JsonArray() });
            }

            return CatalogRoutes(_h)(r);
        };

        await _channel.GetChannelItems(Query("flt:0:y2020"), default);

        Assert.Equal("2020", sent!["year"]!.GetValue<string>());
        Assert.Equal(string.Empty, sent["tags"]!.GetValue<string>());
    }

    private Func<FakeRequest, string> PagedCatalog(Func<int, int, IEnumerable<string>> idsForPage)
        => r =>
        {
            if (r.Path == "v3/filterByContent")
            {
                var page = r.Body["pageNum"]!.GetValue<int>();
                var size = r.Body["pageSize"]!.GetValue<int>();
                return _h.Ok(new JsonObject
                {
                    ["assetList"] = new JsonArray(idsForPage(page, size).Select(id => (JsonNode?)Content(id, "Movie " + id)).ToArray()),
                });
            }

            return CatalogRoutes(_h)(r);
        };

    [Fact]
    public async Task A_year_folder_pages_through_the_whole_year_not_just_the_newest_200()
    {
        // 200 + 200 + 50: three pages, the short third one ends it.
        _h.Route = PagedCatalog((page, size) => Enumerable.Range(0, page < 3 ? size : 50).Select(i => $"P{page}M{i}"));

        var result = await _channel.GetChannelItems(Query("flt:0:y2020"), default);

        Assert.Equal(450, result.TotalRecordCount);
        Assert.Equal(new[] { 1, 2, 3 }, _h.Transport.Requests.Where(r => r.Path == "v3/filterByContent").Select(r => r.Body["pageNum"]!.GetValue<int>()));
        Assert.All(_h.Transport.Requests.Where(r => r.Path == "v3/filterByContent"), r => Assert.Equal("2020", r.Body["year"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_year_folder_stops_if_the_portal_serves_the_same_page_again()
    {
        // A portal ignoring pageNum would otherwise loop to the 5,000-item cap on duplicates.
        _h.Route = PagedCatalog((_, size) => Enumerable.Range(0, size).Select(i => $"M{i}"));

        var result = await _channel.GetChannelItems(Query("flt:0:y2020"), default);

        Assert.Equal(CatalogBrowser.ListingSize, result.TotalRecordCount);
        Assert.Equal(2, _h.Transport.Count("v3/filterByContent"));
    }

    [Fact]
    public async Task Todo_and_genre_folders_stay_at_one_page_of_the_newest_titles()
    {
        _h.Route = PagedCatalog((page, size) => Enumerable.Range(0, size).Select(i => $"P{page}M{i}"));

        var result = await _channel.GetChannelItems(Query("flt:0:all"), default);

        Assert.Equal(CatalogBrowser.ListingSize, result.TotalRecordCount);
        Assert.Equal(1, _h.Transport.Count("v3/filterByContent"));
    }

    [Fact]
    public async Task Years_folder_lists_one_folder_per_year_from_the_shared_vocabulary()
    {
        _h.Route = CatalogRoutes(_h, years: new[] { 2020, 2019 });

        var result = await _channel.GetChannelItems(Query("flt:0:years"), default);

        Assert.Equal(new[] { "flt:0:y2020", "flt:0:y2019" }, result.Items.Select(i => i.Id));
        Assert.Equal(new[] { "2020", "2019" }, result.Items.Select(i => i.Name));

        var byId = result.Items.ToDictionary(i => i.Id);
        Assert.Equal(Collage("2020", "Cover", "https://example.test/thumb.jpg"), byId["flt:0:y2020"].ImageUrl);
        Assert.Equal(Collage("2019", "Cover", "https://example.test/thumb.jpg"), byId["flt:0:y2019"].ImageUrl);
    }

    [Fact]
    public async Task Movies_play_directly_and_series_are_grouped_into_one_show_folder_per_show()
    {
        _h.Route = r =>
        {
            if (r.Path == "v3/filterByContent")
            {
                return _h.Ok(new JsonObject
                {
                    ["assetList"] = WiringHarness.Array(
                        Content("MOVIE1", "A Movie"),
                        Content("S-T2", "Reacher T2", "teleplay"), // newest-first: T2 appears before T1
                        Content("S-T1", "Reacher T1", "teleplay")),
                });
            }

            return CatalogRoutes(_h)(r);
        };

        var result = await _channel.GetChannelItems(Query("flt:0:all"), default);

        // One show folder, keyed by the show's name (the same whichever season a listing shows first); the later season
        // of the same show collapses into it.
        Assert.Equal(new[] { "mov:MOVIE1", "shw:" + ShowIndex.KeyFor("Reacher T2") }, result.Items.Select(i => i.Id));
        Assert.Equal(ShowIndex.KeyFor("Reacher T2"), ShowIndex.KeyFor("Reacher T1"));
        Assert.Equal(ChannelItemType.Media, result.Items[0].Type);
        Assert.Equal(ChannelItemType.Folder, result.Items[1].Type);
        Assert.Equal(ChannelFolderType.Series, result.Items[1].FolderType);
        // The season suffix is stripped for display, case preserved (not the lowercased grouping key).
        Assert.Equal("Reacher", result.Items[1].Name);
    }

    [Fact]
    public async Task Show_folder_lists_seasons_from_sameSeasonSeriesList_in_order()
    {
        _h.Route = r => r.Path == "v4/getItemData"
            ? _h.Ok(new JsonObject
            {
                ["assetData"] = new JsonObject
                {
                    ["sameSeasonSeriesList"] = WiringHarness.Array(
                        new JsonObject { ["contentId"] = "S2", ["seasonNumber"] = 2 },
                        new JsonObject { ["contentId"] = "S1", ["seasonNumber"] = 1 }),
                },
            })
            : FakePortalTransport.Error("x", r.Path);

        var result = await _channel.GetChannelItems(Query("shw:S2"), default);

        // ssn: (not ser:), so this folder's "Temporada N" name/IndexNumber can't collide with a native ser: folder
        // for the SAME content id and lose to whatever Jellyfin already persisted for that GUID (confirmed live
        // 2026-09-23 -- see VodItemKind.ShowSeason's doc comment).
        Assert.Equal(new[] { "ssn:S1", "ssn:S2" }, result.Items.Select(i => i.Id));
        Assert.Equal(new[] { "Temporada 1", "Temporada 2" }, result.Items.Select(i => i.Name));
        Assert.Equal(new int?[] { 1, 2 }, result.Items.Select(i => i.IndexNumber));
        // Season, not Series -- a Series folder nested directly inside another Series folder (the show) confused
        // Jellyfin's own SeriesMetadataService into synthesizing a bogus "Season Unknown" and, at least once,
        // crashing ChannelManager.GetChannelItemsInternal with a NullReferenceException (confirmed live 2026-09-23,
        // reported by the owner as "series don't play" -- SeasonId came back null on a real episode).
        Assert.All(result.Items, i => Assert.Equal(ChannelFolderType.Season, i.FolderType));

        var body = _h.Transport.Requests.Single(r => r.Path == "v4/getItemData").Body;
        Assert.Equal("S2", body["contentId"]!.GetValue<string>());
        Assert.Equal("0", body["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Show_folder_falls_back_to_a_single_season_when_no_season_list_is_reported()
    {
        _h.Route = r => r.Path == "v4/getItemData"
            ? _h.Ok(new JsonObject { ["assetData"] = new JsonObject() })
            : FakePortalTransport.Error("x", r.Path);

        var result = await _channel.GetChannelItems(Query("shw:ONLY"), default);

        Assert.Equal(new[] { "ssn:ONLY" }, result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Parent_id_and_vocabulary_are_each_resolved_once_across_repeated_opens()
    {
        var portalCalls = 0;
        _h.Route = r =>
        {
            if (r.Path is "getNextColumns" or "getRecommendColumnContents" or "v3/filterGenre")
            {
                portalCalls++;
            }

            return r.Path == "v3/filterByContent"
                ? _h.Ok(new JsonObject { ["assetList"] = new JsonArray() })
                : CatalogRoutes(_h)(r);
        };

        await _channel.GetChannelItems(Query("flt:0:all"), default); // resolves the parent id
        await _channel.GetChannelItems(Query("flt:0:g0"), default); // parent id cached; resolves the vocabulary

        // getNextColumns + getRecommendColumnContents (parent id, once) + v3/filterGenre (vocabulary, once) = 3,
        // not 6 -- neither is re-fetched on the second open.
        Assert.Equal(3, portalCalls);
    }

    [Fact]
    public async Task Catalog_items_carry_overview_year_rating_genres_original_title_and_cast()
    {
        var content = Content("MOVIE1", "A Movie");
        content["description"] = "A synopsis.";
        content["releaseTime"] = "2024-03-15";
        content["score"] = 7.5;
        content["tags"] = "Action,Comedy";
        content["director"] = "Jane Doe";
        content["actorDisplay"] = "Alice, Bob";
        content["alias"] = "Original Title";

        _h.Route = r => r.Path == "v3/filterByContent"
            ? _h.Ok(new JsonObject { ["assetList"] = WiringHarness.Array(content) })
            : CatalogRoutes(_h)(r);

        var result = await _channel.GetChannelItems(Query("flt:0:all"), default);
        var item = Assert.Single(result.Items);

        Assert.Equal("A synopsis.", item.Overview);
        Assert.Equal("Original Title", item.OriginalTitle);
        Assert.Equal(2024, item.ProductionYear);
        Assert.Equal(new DateTime(2024, 3, 15), item.PremiereDate);
        Assert.Equal(7.5f, item.CommunityRating);
        Assert.Equal(new[] { "Acción", "Comedia" }, item.Genres);
        Assert.Equal(
            new[] { ("Jane Doe", Jellyfin.Data.Enums.PersonKind.Director), ("Alice", Jellyfin.Data.Enums.PersonKind.Actor), ("Bob", Jellyfin.Data.Enums.PersonKind.Actor) },
            item.People.Select(p => (p.Name, p.Type)));
    }

    [Fact]
    public async Task Absent_metadata_fields_are_left_at_their_defaults()
    {
        _h.Route = r => r.Path == "v3/filterByContent"
            ? _h.Ok(new JsonObject { ["assetList"] = WiringHarness.Array(Content("MOVIE1", "A Movie")) })
            : CatalogRoutes(_h)(r);

        var result = await _channel.GetChannelItems(Query("flt:0:all"), default);
        var item = Assert.Single(result.Items);

        Assert.Null(item.Overview);
        Assert.Null(item.OriginalTitle);
        Assert.Null(item.ProductionYear);
        Assert.Null(item.PremiereDate);
        Assert.Null(item.CommunityRating);
        Assert.Empty(item.Genres);
        Assert.Empty(item.People);
    }

    [Theory]
    [InlineData("cat:9")]
    [InlineData("flt:9:all")]
    [InlineData("row:9:top")]
    public async Task A_folder_for_a_catalog_that_no_longer_exists_lists_nothing_instead_of_failing(string folderId)
    {
        var result = await _channel.GetChannelItems(Query(folderId), default);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task A_show_has_one_id_whichever_season_a_listing_shows_first_and_opens_through_it()
    {
        var newestFirst = true;
        _h.Route = r =>
        {
            if (r.Path == "v3/filterByContent")
            {
                var t2 = Content("S-T2", "Reacher T2", "teleplay");
                var t1 = Content("S-T1", "Reacher T1", "teleplay");
                return _h.Ok(new JsonObject { ["assetList"] = newestFirst ? WiringHarness.Array(t2, t1) : WiringHarness.Array(t1, t2) });
            }

            if (r.Path == "v4/getItemData")
            {
                return _h.Ok(new JsonObject
                {
                    ["assetData"] = new JsonObject
                    {
                        ["sameSeasonSeriesList"] = WiringHarness.Array(
                            new JsonObject { ["contentId"] = "S-T1", ["seasonNumber"] = 1 },
                            new JsonObject { ["contentId"] = "S-T2", ["seasonNumber"] = 2 }),
                    },
                });
            }

            return CatalogRoutes(_h)(r);
        };

        var first = (await _channel.GetChannelItems(Query("flt:0:all"), default)).Items.Single().Id;
        newestFirst = false;
        var second = (await _channel.GetChannelItems(Query("flt:0:g0"), default)).Items.Single().Id;
        var seasons = await _channel.GetChannelItems(Query(first), default);

        Assert.Equal(first, second); // one Series item in Jellyfin, not one per first-seen season
        Assert.StartsWith("shw:" + ShowIndex.KeyPrefix, first);
        Assert.Equal(new[] { "ssn:S-T1", "ssn:S-T2" }, seasons.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_show_key_the_index_doesnt_know_lists_nothing_instead_of_failing()
    {
        var result = await _channel.GetChannelItems(Query("shw:" + ShowIndex.KeyFor("Nunca Vista")), default);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Tmdb_destacado_rows_are_listed_by_label_key_and_open_to_the_portals_titles()
    {
        var h = new WiringHarness(withTmdb: true, featuredRows: "Tendencias | trending; Apple TV+ | discover-tv | network=apple");
        var channel = new PortalitoVodChannel(h, new FakeVodTrackProbe(), new SubtitleFileCache(_subtitleDir, new HttpClient(_subtitleHost)), _collages, NullLogger<PortalitoVodChannel>.Instance);
        h.Tmdb.Body = (path, q) => q.GetValueOrDefault("page") != "1" ? "{\"results\":[]}"
            : path == "trending/all/week"
                ? "{\"results\":[{\"id\":1,\"media_type\":\"movie\",\"title\":\"Duna\",\"original_title\":\"Dune\",\"release_date\":\"2021-09-15\"}]}"
                : "{\"results\":[]}";
        var dune = Content("DUNE1", "Duna");
        dune["alias"] = "Dune";
        dune["releaseTime"] = "2021-10-01";
        h.Route = r => r.Path == "v3/searchByName"
            ? h.Ok(new JsonObject { ["searchItemList"] = WiringHarness.Array(new JsonObject { ["itemList"] = WiringHarness.Array((JsonObject)dune.DeepClone()) }) })
            : FakePortalTransport.Error("x", r.Path);

        var rows = await channel.GetChannelItems(Query("fea:root"), default);
        var trending = rows.Items.Single(i => i.Name == "Tendencias").Id;
        var titles = await channel.GetChannelItems(Query(trending), default);
        var stale = await channel.GetChannelItems(Query("row:0:tmdb"), default); // a position id from before 0.1.1.4

        Assert.Equal("row:" + DiscoveryBrowser.RowKey("Tendencias") + ":tmdb", trending);
        Assert.Equal(new[] { "mov:DUNE1" }, titles.Items.Select(i => i.Id));
        Assert.Empty(stale.Items);
    }
}
