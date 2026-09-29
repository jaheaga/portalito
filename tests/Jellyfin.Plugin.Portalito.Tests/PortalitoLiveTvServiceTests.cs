using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;
using Jellyfin.Plugin.Portalito.Live;
using Jellyfin.Plugin.Portalito.Portal;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class PortalitoLiveTvServiceTests
{
    private readonly WiringHarness _h = new();
    private readonly PortalitoLiveTvService _service;

    public PortalitoLiveTvServiceTests() => _service = new PortalitoLiveTvService(_h);

    private static (long E, string S) Params(string url)
    {
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        return (long.Parse(q["e"]!), q["s"]!);
    }

    [Fact]
    public async Task Channels_are_mapped_from_the_portal_channel_list()
    {
        _h.Route = r => r.Path == "v6/getLiveData"
            ? _h.Ok(new JsonObject
            {
                ["channelList"] = WiringHarness.Array(
                    new JsonObject { ["channelCode"] = "cx_a_720p", ["name"] = "Canal Uno", ["channelNumber"] = 1 },
                    new JsonObject { ["channelCode"] = "cx_b_720p", ["name"] = "Canal Dos", ["channelNumber"] = "2" }),
            })
            : FakePortalTransport.Error("x", r.Path);

        var channels = (await _service.GetChannelsAsync(default)).ToList();

        Assert.Equal(2, channels.Count);
        Assert.Equal("cx_a_720p", channels[0].Id);
        Assert.Equal("Canal Uno", channels[0].Name);
        Assert.Equal("1", channels[0].Number);
        Assert.Equal(ChannelType.TV, channels[0].ChannelType);
        Assert.Equal("2", channels[1].Number);
    }

    [Fact]
    public async Task Channels_without_a_code_are_skipped_and_a_missing_name_falls_back_to_the_code()
    {
        _h.Route = _ => _h.Ok(new JsonObject
        {
            ["channelList"] = WiringHarness.Array(
                new JsonObject { ["name"] = "No code" },
                new JsonObject { ["channelCode"] = "" },
                new JsonObject { ["channelCode"] = "cx_c_720p" }),
        });

        var channels = (await _service.GetChannelsAsync(default)).ToList();

        var only = Assert.Single(channels);
        Assert.Equal("cx_c_720p", only.Id);
        Assert.Equal("cx_c_720p", only.Name);
        Assert.Null(only.Number);
    }

    [Fact]
    public async Task A_missing_channel_list_yields_no_channels()
    {
        _h.Route = _ => _h.Ok(new JsonObject { ["other"] = 1 });

        Assert.Empty(await _service.GetChannelsAsync(default));
    }

    [Fact]
    public async Task A_portal_failure_surfaces_as_a_portal_exception()
    {
        _h.Route = r => FakePortalTransport.Error("bad", "boom");

        await Assert.ThrowsAsync<PortalException>(() => _service.GetChannelsAsync(default));
    }

    [Fact]
    public async Task Channel_stream_points_at_the_signed_local_playlist_proxy_without_calling_the_portal()
    {
        var source = await _service.GetChannelStream("cx_a_720p", "ignored", default);

        Assert.StartsWith(WiringHarness.ProxyBase + "/Portalito/live/cx_a_720p.m3u8?e=", source.Path);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.True(source.IsInfiniteStream);
        Assert.True(source.IsRemote);
        Assert.Equal("cx_a_720p", source.Id);
        Assert.Empty(_h.Transport.Requests);
    }

    [Fact]
    public async Task Channel_stream_is_never_offered_for_direct_play_because_clients_cannot_reach_the_proxy()
    {
        var source = await _service.GetChannelStream("cx_a_720p", "x", default);

        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Contains(source.MediaStreams, s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        Assert.Contains(source.MediaStreams, s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio);
    }

    [Fact]
    public async Task Playlist_url_signature_verifies_for_its_channel_only_and_outlives_a_long_viewing_session()
    {
        var url = (await _service.GetChannelStream("cx_a_720p", "x", default)).Path;
        var (e, s) = Params(url);

        Assert.True(_h.Signer.VerifyLivePlaylist("cx_a_720p", e, s));
        Assert.False(_h.Signer.VerifyLivePlaylist("cx_b_720p", e, s));

        // ffmpeg keeps re-fetching this same URL for as long as the channel plays: a channel left on overnight
        // must not hit its expiry (it used to, at 12 hours).
        _h.Clock.Now += TimeSpan.FromHours(13);
        Assert.True(_h.Signer.VerifyLivePlaylist("cx_a_720p", e, s));
        _h.Clock.Now += TimeSpan.FromDays(7);
        Assert.False(_h.Signer.VerifyLivePlaylist("cx_a_720p", e, s));
    }

    [Fact]
    public async Task Channels_carry_their_logo_through_the_image_proxy_and_an_hd_flag()
    {
        _h.Route = _ => _h.Ok(new JsonObject
        {
            ["channelList"] = WiringHarness.Array(
                new JsonObject { ["channelCode"] = "cx_a_720p", ["name"] = "Canal Uno", ["posterUrl"] = "https://img.test/uno.jpg" },
                new JsonObject { ["channelCode"] = "cx_b", ["name"] = "ESPN FHD" },
                new JsonObject { ["channelCode"] = "cx_c", ["name"] = "Canal Tres" }),
        });

        var channels = (await _service.GetChannelsAsync(default)).ToList();

        // Jellyfin's Live TV section had no logos at all: the portal's posterUrl never reached ChannelInfo.
        Assert.Equal(_h.Signer.ImageUrl("https://img.test/uno.jpg", MediaSources.ImageUrlValidity), channels[0].ImageUrl);
        Assert.True(channels[0].HasImage);
        Assert.Null(channels[1].ImageUrl);
        Assert.Null(channels[1].HasImage);

        Assert.True(channels[0].IsHD); // code suffix _720p
        Assert.True(channels[1].IsHD); // "FHD" in the name
        Assert.Null(channels[2].IsHD); // nothing says either way
    }

    [Fact]
    public async Task Guide_comes_from_the_portals_program_list()
    {
        var fixture = EpgMapperTests.Fixture();
        _h.Route = r => r.Path == "v3/getProgram" && r.Body["channelCode"]!.GetValue<string>() == "cx_nba"
            ? _h.Ok(fixture)
            : FakePortalTransport.Error("x", r.Path);

        var programs = (await _service.GetProgramsAsync(
            "cx_nba",
            new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            default)).ToList();

        Assert.NotEmpty(programs);
        Assert.All(programs, p => Assert.Equal("cx_nba", p.ChannelId));
        Assert.All(programs, p => Assert.True(p.EndDate > new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task A_channel_the_portal_has_no_guide_for_is_empty_not_an_error()
    {
        // Jellyfin refreshes the guide channel by channel; one failure must not abort the rest.
        _h.Route = r => FakePortalTransport.Error("epg404", "no program");

        Assert.Empty(await _service.GetProgramsAsync("cx_x", DateTime.UtcNow, DateTime.UtcNow.AddDays(1), default));
    }

    [Fact]
    public async Task Channels_whose_code_the_proxy_would_refuse_are_not_listed()
    {
        _h.Route = _ => _h.Ok(new JsonObject
        {
            ["channelList"] = WiringHarness.Array(
                new JsonObject { ["channelCode"] = "bad/code", ["name"] = "Unsafe" },
                new JsonObject { ["channelCode"] = "cx_ok_720p", ["name"] = "Fine" }),
        });

        var channels = (await _service.GetChannelsAsync(default)).ToList();

        Assert.Equal("cx_ok_720p", Assert.Single(channels).Id);
    }

    [Fact]
    public async Task Media_source_list_has_one_entry_matching_the_channel_stream()
    {
        var sources = await _service.GetChannelStreamMediaSources("cx_a_720p", default);

        var only = Assert.Single(sources);
        Assert.Equal("cx_a_720p", only.Id);
        Assert.Contains("/Portalito/live/cx_a_720p.m3u8", only.Path);
    }

    [Fact]
    public async Task Closing_and_resetting_are_no_ops()
    {
        await _service.CloseLiveStream("x", default);
        await _service.ResetTuner("x", default);

        Assert.Empty(_h.Transport.Requests);
    }

    [Fact]
    public async Task Recording_is_reported_as_unsupported_while_listing_timers_is_harmless()
    {
        Assert.Empty(await _service.GetTimersAsync(default));
        Assert.Empty(await _service.GetSeriesTimersAsync(default));
        Assert.NotNull(await _service.GetNewTimerDefaultsAsync(default));

        await Assert.ThrowsAsync<NotSupportedException>(() => _service.CreateTimerAsync(new TimerInfo(), default));
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.CreateSeriesTimerAsync(new SeriesTimerInfo(), default));
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.UpdateTimerAsync(new TimerInfo(), default));
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.UpdateSeriesTimerAsync(new SeriesTimerInfo(), default));
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.CancelTimerAsync("t", default));
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.CancelSeriesTimerAsync("t", default));
    }

    [Fact]
    public void Identifies_itself_to_jellyfin()
    {
        Assert.Equal("Portalito", _service.Name);
        Assert.NotNull(_service.HomePageUrl);
    }
}
