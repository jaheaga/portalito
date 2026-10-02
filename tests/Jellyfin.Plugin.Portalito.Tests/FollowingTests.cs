using System.Text.Json.Nodes;
using System.Xml.Linq;
using Jellyfin.Plugin.Portalito.Configuration;
using Jellyfin.Plugin.Portalito.Following;
using Jellyfin.Plugin.Portalito.Proxy;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>The "Portalito · Siguiendo" library (Next Up): its files, which shows it follows, and carrying progress over.</summary>
public class FollowingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "portalito-following-" + Guid.NewGuid().ToString("N"));
    private readonly ProxyUrlSigner _signer = new("secret", "http://127.0.0.1:8096", TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static MirrorShow Show(params MirrorEpisode[] episodes)
        => new("111", "Alquimia de Almas", episodes, "Plot & more", 2022, new[] { "Drama" }, "http://img/poster.jpg");

    private static MirrorEpisode Episode(int season, int number, string? image = null)
        => new("S" + season, "E" + season + "_" + number, season, number, "Episodio " + number, RunTimeTicks: TimeSpan.FromMinutes(69.8).Ticks, ImageUrl: image);

    // ---- files ----

    [Fact]
    public void A_show_folder_is_safe_and_tagged_with_its_id()
    {
        Assert.Equal("Alquimia de Almas [portalito-111]", FollowFiles.ShowFolder("Alquimia de Almas", "111"));
        Assert.Equal("AC DC live [portalito-9]", FollowFiles.ShowFolder("AC/DC: live?", "9"));
        Assert.Equal("Serie [portalito-9]", FollowFiles.ShowFolder(" ... ", "9"));
    }

    [Fact]
    public void Episodes_are_laid_out_the_way_Jellyfins_tv_resolver_reads_them()
    {
        var files = FollowFiles.TextFiles(Show(Episode(1, 5), Episode(2, 1)), _signer.PlayUrl);

        Assert.Equal(
            new[] { "Season 01/S01E05.nfo", "Season 01/S01E05.strm", "Season 02/S02E01.nfo", "Season 02/S02E01.strm", "tvshow.nfo" },
            files.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void A_strm_holds_a_play_url_that_reads_back_to_its_ids()
    {
        var strm = FollowFiles.TextFiles(Show(Episode(1, 5)), _signer.PlayUrl)["Season 01/S01E05.strm"];

        Assert.StartsWith("http://127.0.0.1:8096/Portalito/play/E1_5?series=S1&s=", strm);
        Assert.Equal(("E1_5", "S1"), FollowFiles.ParseStrm(strm));
        Assert.Null(FollowFiles.ParseStrm("http://127.0.0.1:8096/Portalito/vod/E1_5?e=1&s=aa"));
        Assert.Null(FollowFiles.ParseStrm("not a url"));
    }

    [Fact]
    public void The_nfo_files_carry_numbering_runtime_and_escaped_text()
    {
        var show = Show(Episode(2, 7));
        var tvshow = XElement.Parse(FollowFiles.ShowNfo(show).Split('\n', 2)[1]);
        var episode = XElement.Parse(FollowFiles.EpisodeNfo(show, show.Episodes[0]).Split('\n', 2)[1]);

        Assert.Equal("Alquimia de Almas", tvshow.Element("title")!.Value);
        Assert.Equal("Plot & more", tvshow.Element("plot")!.Value);
        Assert.Equal("111", tvshow.Element("uniqueid")!.Value);
        Assert.Equal("2", episode.Element("season")!.Value);
        Assert.Equal("7", episode.Element("episode")!.Value);
        Assert.Equal("70", episode.Element("runtime")!.Value);
        Assert.Equal("E2_7", episode.Element("uniqueid")!.Value);
        Assert.Contains("Plot &amp; more", FollowFiles.ShowNfo(show));
    }

    [Fact]
    public void A_show_is_keyed_by_a_custom_id_and_carries_the_hidden_tag()
    {
        var tvshow = XElement.Parse(FollowFiles.ShowNfo(Show(Episode(1, 1))).Split('\n', 2)[1]);

        // Jellyfin keys watch data by a Custom id when there is one, so moving the folder keeps the progress.
        Assert.Equal("portalito-111", tvshow.Elements("uniqueid").Single(e => (string?)e.Attribute("type") == "Custom").Value);
        Assert.Equal(FollowFiles.HiddenTag, tvshow.Element("tag")!.Value);
    }

    [Fact]
    public void Users_who_cant_open_the_channel_get_the_hidden_tag_blocked_and_lose_it_when_they_can()
    {
        Assert.Equal(new[] { "kids", FollowFiles.HiddenTag }, FollowLibrary.BlockTag(seesChannel: false, new[] { "kids" }));
        Assert.Null(FollowLibrary.BlockTag(seesChannel: false, new[] { FollowFiles.HiddenTag.ToUpperInvariant() }));
        Assert.Equal(new[] { "kids" }, FollowLibrary.BlockTag(seesChannel: true, new[] { "kids", FollowFiles.HiddenTag }));
        Assert.Null(FollowLibrary.BlockTag(seesChannel: true, new[] { "kids" }));
    }

    [Fact]
    public void Channel_stops_queue_one_sync_per_debounce_window()
    {
        long last = 0;
        var t0 = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc).Ticks;

        Assert.True(FollowTrigger.ShouldQueue(ref last, t0));
        Assert.False(FollowTrigger.ShouldQueue(ref last, t0 + TimeSpan.FromSeconds(30).Ticks));
        Assert.True(FollowTrigger.ShouldQueue(ref last, t0 + FollowTrigger.Debounce.Ticks));
    }

    [Theory]
    [InlineData("Alquimia de Almas T1_05", "Alquimia de Almas", 5, "Episodio 5")]
    [InlineData("El reencuentro", "Alquimia de Almas", 5, "El reencuentro")]
    [InlineData(null, "Alquimia de Almas", 3, "Episodio 3")]
    [InlineData("  ", null, 2, "Episodio 2")]
    public void Episode_titles_that_only_repeat_the_show_become_episodio_n(string? name, string? show, int number, string expected)
        => Assert.Equal(expected, FollowFiles.EpisodeTitle(name, show, number));

    [Fact]
    public void A_show_id_reads_back_from_its_folder_name()
    {
        Assert.Equal("3A0232FD", FollowFiles.ShowIdOfFolder(FollowFiles.ShowFolder("Naruto Shippuden", "3A0232FD")));
        Assert.Null(FollowFiles.ShowIdOfFolder("Season 01"));
        Assert.Null(FollowFiles.ShowIdOfFolder(null));
    }

    [Fact]
    public void Subtitle_sidecars_are_named_by_language_one_per_language()
    {
        var taken = new HashSet<string>();

        Assert.Equal("S01E168.spa.srt", FollowFiles.SubtitleFileName("S01E168", "spa", "srt", taken));
        Assert.Equal("S01E168.eng.vtt", FollowFiles.SubtitleFileName("S01E168", "ENG", "vtt", taken));
        Assert.Null(FollowFiles.SubtitleFileName("S01E168", "spa", "srt", taken));
        Assert.Equal("S01E168.und.srt", FollowFiles.SubtitleFileName("S01E168", "../", "srt", taken));
    }

    [Fact]
    public void Evicting_a_folder_drops_it_and_its_files_from_jellyfins_directory_cache()
    {
        var service = (MediaBrowser.Controller.Providers.DirectoryService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MediaBrowser.Controller.Providers.DirectoryService));
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal);
        cache["/lib/Show/Season 01"] = new List<string>();
        cache["/lib/Show/Season 01/S01E01.strm"] = new List<string>();
        cache["/lib/Show/Season 010"] = new List<string>();
        typeof(MediaBrowser.Controller.Providers.DirectoryService).GetField("_filePathCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, cache);

        FollowSubtitles.Evict(service, "/lib/Show/Season 01");

        Assert.Equal(new[] { "/lib/Show/Season 010" }, cache.Keys);
    }

    // ---- writer ----

    private FollowWriter Writer(List<string>? downloads = null, byte[]? image = null)
        => new(_root, (url, _) =>
        {
            downloads?.Add(url);
            return Task.FromResult(image);
        });

    [Fact]
    public async Task Writing_twice_changes_nothing_the_second_time()
    {
        var writer = Writer();
        var show = Show(Episode(1, 1), Episode(1, 2));

        Assert.True(await writer.WriteAsync(show, _signer.PlayUrl, null, default));
        Assert.False(await writer.WriteAsync(show, _signer.PlayUrl, null, default));
        Assert.True(File.Exists(Path.Combine(_root, "Alquimia de Almas [portalito-111]", "Season 01", "S01E02.strm")));
    }

    [Fact]
    public async Task An_episode_the_portal_dropped_is_removed_with_its_thumb_and_empty_season()
    {
        var writer = Writer(image: new byte[] { 0xFF, 0xD8, 0xFF });
        await writer.WriteAsync(Show(Episode(1, 1), Episode(2, 1, "http://img/e21.jpg")), _signer.PlayUrl, null, default);
        var dir = Path.Combine(_root, "Alquimia de Almas [portalito-111]");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not ours");
        Assert.True(File.Exists(Path.Combine(dir, "Season 02", "S02E01-thumb.jpg")));

        Assert.True(await writer.WriteAsync(Show(Episode(1, 1)), _signer.PlayUrl, null, default));

        Assert.False(Directory.Exists(Path.Combine(dir, "Season 02")));
        Assert.True(File.Exists(Path.Combine(dir, "Season 01", "S01E01.strm")));
        Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
    }

    [Fact]
    public async Task Images_are_fetched_once_and_named_by_their_bytes()
    {
        var downloads = new List<string>();
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0 };
        var writer = Writer(downloads, png);
        var show = Show(Episode(1, 1, "http://img/poster.jpg"), Episode(1, 2, "http://img/e12.jpg"));

        await writer.WriteAsync(show, _signer.PlayUrl, null, default);
        await writer.WriteAsync(show, _signer.PlayUrl, null, default);

        // The episode whose image is the show's own poster gets no thumb of its own.
        Assert.Equal(new[] { "http://img/poster.jpg", "http://img/e12.jpg" }, downloads);
        Assert.True(File.Exists(Path.Combine(_root, "Alquimia de Almas [portalito-111]", "poster.png")));
        Assert.True(File.Exists(Path.Combine(_root, "Alquimia de Almas [portalito-111]", "Season 01", "S01E02-thumb.png")));
    }

    [Fact]
    public async Task A_failed_image_is_skipped_and_retried_next_time()
    {
        var calls = 0;
        var writer = new FollowWriter(_root, (_, _) => ++calls == 1 ? throw new HttpRequestException("down") : Task.FromResult<byte[]?>(new byte[] { 1 }));

        await writer.WriteAsync(Show(Episode(1, 1)), _signer.PlayUrl, null, default);
        Assert.False(Directory.EnumerateFiles(Path.Combine(_root, "Alquimia de Almas [portalito-111]"), "poster.*").Any());

        Assert.True(await writer.WriteAsync(Show(Episode(1, 1)), _signer.PlayUrl, null, default));
        Assert.True(File.Exists(Path.Combine(_root, "Alquimia de Almas [portalito-111]", "poster.jpg")));
    }

    [Fact]
    public async Task A_show_gets_the_landscape_art_a_library_series_has_and_keeps_it()
    {
        var downloads = new List<string>();
        var writer = Writer(downloads, new byte[] { 0xFF, 0xD8, 0xFF });
        var show = Show(Episode(1, 1)) with { BackdropUrl = "http://img/back.jpg", ThumbUrl = "http://img/land.jpg", LogoUrl = "http://img/logo.png" };

        await writer.WriteAsync(show, _signer.PlayUrl, null, default);
        await writer.WriteAsync(show, _signer.PlayUrl, null, default);

        var dir = Path.Combine(_root, "Alquimia de Almas [portalito-111]");
        foreach (var name in new[] { "poster", "backdrop", "landscape", "logo" })
        {
            Assert.True(Directory.EnumerateFiles(dir, name + ".*").Any(), name);
        }

        Assert.Equal(4, downloads.Count); // fetched once, kept on the next write
    }

    [Fact]
    public void A_tmdb_still_is_used_only_when_tmdb_numbers_the_season_the_same_way()
    {
        var stills = new Dictionary<int, string> { [3] = "http://img/s3.jpg" };

        Assert.Equal("http://img/s3.jpg", FollowFiles.StillFor(3, portalSeasonCount: 10, tmdbSeasonCount: 10, stills));
        Assert.Null(FollowFiles.StillFor(3, portalSeasonCount: 500, tmdbSeasonCount: 10, stills)); // one portal season of 500
        Assert.Null(FollowFiles.StillFor(3, portalSeasonCount: 10, tmdbSeasonCount: 0, stills)); // TMDB has no such season
        Assert.Null(FollowFiles.StillFor(4, portalSeasonCount: 10, tmdbSeasonCount: 10, stills)); // no still yet
    }

    [Fact]
    public void The_portal_landscape_image_is_its_poster_file_type()
    {
        var content = new JsonObject
        {
            ["posterList"] = new JsonArray(
                new JsonObject { ["fileType"] = "icon", ["fileUrl"] = "http://img/icon.jpg" },
                new JsonObject { ["fileType"] = "poster", ["fileUrl"] = "http://img/wide.jpg" }),
        };

        Assert.Equal("http://img/wide.jpg", Jellyfin.Plugin.Portalito.Catalog.PortalJson.BackdropUrl(content));
        Assert.Null(Jellyfin.Plugin.Portalito.Catalog.PortalJson.BackdropUrl(new JsonObject()));
    }

    [Fact]
    public async Task A_renamed_show_moves_to_its_new_folder()
    {
        var writer = Writer();
        await writer.WriteAsync(Show(Episode(1, 1)), _signer.PlayUrl, null, default);
        var renamed = Show(Episode(1, 1)) with { Name = "Alquimia" };

        await writer.WriteAsync(renamed, _signer.PlayUrl, "Alquimia de Almas [portalito-111]", default);

        Assert.False(Directory.Exists(Path.Combine(_root, "Alquimia de Almas [portalito-111]")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Alquimia [portalito-111]")));
    }

    // ---- which shows ----

    [Fact]
    public void Shows_played_in_the_last_60_days_or_favorited_are_followed_most_recent_first()
    {
        var plan = FollowPlanner.Plan(
            new[]
            {
                new ShowActivity("old", Now.AddDays(-61)),
                new ShowActivity("recent", Now.AddDays(-1)),
                new ShowActivity("older", Now.AddDays(-30)),
                new ShowActivity("older", Now.AddDays(-90)),
                new ShowActivity("fav", null, Favorite: true),
                new ShowActivity("never", null),
            },
            Now);

        Assert.Equal(new[] { "fav", "recent", "older" }, plan);
    }

    [Fact]
    public void The_number_of_followed_shows_is_capped()
    {
        var activity = Enumerable.Range(0, FollowPlanner.MaxShows + 20).Select(i => new ShowActivity("s" + i, Now.AddMinutes(-i)));

        var plan = FollowPlanner.Plan(activity, Now);

        Assert.Equal(FollowPlanner.MaxShows, plan.Count);
        Assert.Equal("s0", plan[0]);
    }

    // ---- watch state ----

    private static UserItemData Data(bool played = false, long position = 0, DateTime? last = null, int count = 0)
        => new() { Key = "k", Played = played, PlaybackPositionTicks = position, LastPlayedDate = last, PlayCount = count };

    [Fact]
    public void Newer_channel_progress_is_copied_to_the_library_copy()
    {
        var channel = Data(position: 500, last: Now, count: 2);
        var library = Data(played: true, last: Now.AddDays(-1), count: 1);

        Assert.True(WatchState.ShouldCopy(channel, library));
        WatchState.Copy(channel, library);

        Assert.False(library.Played);
        Assert.Equal(500, library.PlaybackPositionTicks);
        Assert.Equal(Now, library.LastPlayedDate);
        Assert.Equal(2, library.PlayCount);
    }

    [Fact]
    public void Older_or_empty_channel_progress_is_not_copied()
    {
        Assert.False(WatchState.ShouldCopy(Data(played: true, last: Now.AddDays(-2)), Data(position: 9, last: Now)));
        Assert.False(WatchState.ShouldCopy(Data(last: Now), Data()));
        Assert.False(WatchState.ShouldCopy(Data(played: true), Data()));
        Assert.False(WatchState.ShouldCopy(Data(played: true, last: Now), Data(last: Now)));
    }

    // ---- store, paths, playback ----

    [Fact]
    public void The_store_survives_a_restart_and_finds_shows_by_season_and_folder()
    {
        var path = Path.Combine(_root, "following.json");
        new FollowStore(path).Set(new FollowedShow("111", "Alquimia", "Alquimia [portalito-111]", new[] { "111", "222" }, Now, Now));

        var store = new FollowStore(path);

        Assert.Equal("111", store.ShowForSeason("222")!.ShowId);
        Assert.Equal("111", store.ShowForFolder("Alquimia [portalito-111]")!.ShowId);
        Assert.Null(store.ShowForSeason("333"));
        store.Remove("111");
        Assert.Empty(new FollowStore(path).All);
    }

    [Fact]
    public void The_library_folder_defaults_under_the_data_folder()
    {
        var data = Path.Combine(_root, "data");

        Assert.Equal(Path.Combine(data, "portalito", "siguiendo"), FollowLibrary.Root(new PluginConfiguration(), data));
        Assert.Equal(Path.Combine(_root, "x"), FollowLibrary.Root(new PluginConfiguration { FollowLibraryPath = " " + Path.Combine(_root, "x") + " " }, data));
        Assert.True(new PluginConfiguration().FollowLibraryEnabled);
    }

    [Fact]
    public void Only_files_inside_the_library_folder_belong_to_it()
    {
        var root = Path.Combine(_root, "siguiendo");

        Assert.True(FollowLibrary.Contains(root, Path.Combine(root, "Show [portalito-1]", "Season 01", "S01E01.strm")));
        Assert.False(FollowLibrary.Contains(root, root + "-other" + Path.DirectorySeparatorChar + "a.strm"));
        Assert.False(FollowLibrary.Contains(root, null));
    }

    [Fact]
    public void The_filter_targets_both_playback_info_calls()
    {
        Assert.True(FollowPlaybackFilter.IsPlaybackInfo(new ControllerActionDescriptor { ControllerName = "MediaInfo", ActionName = "GetPostedPlaybackInfo" }));
        Assert.True(FollowPlaybackFilter.IsPlaybackInfo(new ControllerActionDescriptor { ControllerName = "MediaInfo", ActionName = "GetPlaybackInfo" }));
        Assert.False(FollowPlaybackFilter.IsPlaybackInfo(new ControllerActionDescriptor { ControllerName = "Items", ActionName = "GetItems" }));
        Assert.False(FollowPlaybackFilter.IsPlaybackInfo(null));
    }

    [Fact]
    public void The_returned_sources_are_never_direct_playable()
    {
        var response = new MediaBrowser.Model.MediaInfo.PlaybackInfoResponse
        {
            MediaSources = new[] { new MediaBrowser.Model.Dto.MediaSourceInfo { SupportsDirectPlay = true, SupportsDirectStream = true, SupportsTranscoding = true } },
        };

        FollowPlaybackFilter.ForceServerStreaming(response);

        Assert.All(response.MediaSources, m => Assert.False(m.SupportsDirectPlay || m.SupportsDirectStream));
        Assert.True(response.MediaSources[0].SupportsTranscoding);
    }

    [Fact]
    public void Subtitles_handed_out_as_a_disk_path_are_served_by_jellyfin_instead()
    {
        MediaStream Sub(int index, string url) => new()
        {
            Type = MediaStreamType.Subtitle, Index = index, Codec = "subrip", IsExternal = true, IsExternalUrl = true,
            DeliveryMethod = SubtitleDeliveryMethod.External, DeliveryUrl = url,
        };
        var response = new MediaBrowser.Model.MediaInfo.PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaBrowser.Model.Dto.MediaSourceInfo
                {
                    Id = "7e9a3a89d4d1743ed9b97235c1cd94dd",
                    MediaStreams = new List<MediaStream>
                    {
                        Sub(0, "/config/data/portalito/siguiendo/Show/Season 01/S01E03.eng.srt"),
                        Sub(1, "/Videos/x/y/Subtitles/1/0/Stream.srt?ApiKey=k"),
                        Sub(2, "https://cdn.example/sub.srt"),
                        new() { Type = MediaStreamType.Audio, Index = 3 },
                    },
                },
            },
        };

        FollowPlaybackFilter.ServeSubtitlesFromServer(response, Guid.Parse("7e9a3a89d4d1743ed9b97235c1cd94dd"), "tok");

        var streams = response.MediaSources[0].MediaStreams;
        Assert.Equal("/Videos/7e9a3a89-d4d1-743e-d9b9-7235c1cd94dd/7e9a3a89d4d1743ed9b97235c1cd94dd/Subtitles/0/0/Stream.subrip?ApiKey=tok", streams[0].DeliveryUrl);
        Assert.False(streams[0].IsExternalUrl);
        Assert.Equal("/Videos/x/y/Subtitles/1/0/Stream.srt?ApiKey=k", streams[1].DeliveryUrl); // already Jellyfin's link
        Assert.Equal("https://cdn.example/sub.srt", streams[2].DeliveryUrl); // a real remote subtitle
        Assert.Null(streams[3].DeliveryUrl);
    }

    [Fact]
    public void The_channel_resume_point_is_cleared_only_while_the_library_holds_the_same_play()
    {
        var channel = Data(position: 500, last: Now);

        Assert.False(WatchState.ClearChannelResume(channel, Data(last: Now.AddMinutes(-5)))); // the channel moved on since
        Assert.Equal(500, channel.PlaybackPositionTicks);
        Assert.True(WatchState.ClearChannelResume(channel, Data(position: 500, last: Now)));
        Assert.Equal(0, channel.PlaybackPositionTicks);
        Assert.False(WatchState.ClearChannelResume(channel, Data(last: Now))); // nothing left to clear
    }
}
