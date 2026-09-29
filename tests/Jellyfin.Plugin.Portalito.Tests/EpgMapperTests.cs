using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Live;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>The guide mapper, against a synthetic <c>v3/getProgram</c> payload shaped like the real one.</summary>
public class EpgMapperTests
{
    private static readonly DateTime Ever = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Never = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A synthetic day of guide data with the quirks the mapper must handle: 59 back-to-back blocks, each titled with
    /// its own clock time (so the mapper substitutes the channel name), and a first block that ends at
    /// <c>...235959</c> ("until midnight") so the next one starts with no gap. Built in code -- no captured data.
    /// </summary>
    internal static JsonObject Fixture()
    {
        var list = new JsonArray();
        // Block 0: 22:00 -> 23:59:59 on 2026-09-14 (the "...235959 means midnight" case).
        list.Add(new JsonObject { ["contentId"] = "c0", ["programName"] = "22:00", ["startTime"] = "20260914220000", ["endTime"] = "20260914235959" });
        // Blocks 1..58: two-hour slots from 2026-09-15 00:00, each named by its start clock time.
        var cursor = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i < 59; i++)
        {
            var end = cursor.AddHours(2);
            list.Add(new JsonObject
            {
                ["contentId"] = "c" + i,
                ["programName"] = cursor.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                ["startTime"] = cursor.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture),
                ["endTime"] = end.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture),
            });
            cursor = end;
        }

        return new JsonObject { ["name"] = "NBA Eventos", ["programList"] = list };
    }

    private static JsonObject Payload(params (string Start, string End, string Name)[] programs) => new()
    {
        ["name"] = "Canal Uno",
        ["programList"] = new JsonArray(programs.Select((p, i) => (JsonNode?)new JsonObject
        {
            ["contentId"] = "c" + i,
            ["programName"] = p.Name,
            ["startTime"] = p.Start,
            ["endTime"] = p.End,
        }).ToArray()),
    };

    [Fact]
    public void Maps_every_program_of_the_captured_channel_in_order_without_gaps()
    {
        var programs = EpgMapper.Map(Fixture(), "cx_nba", TimeZoneInfo.Utc, Ever, Never);

        Assert.Equal(59, programs.Count);
        Assert.All(programs, p => Assert.Equal("cx_nba", p.ChannelId));
        Assert.Equal(59, programs.Select(p => p.Id).Distinct().Count());
        Assert.Equal(new DateTime(2026, 9, 14, 22, 0, 0, DateTimeKind.Utc), programs[0].StartDate);
        Assert.Equal(DateTimeKind.Utc, programs[0].StartDate.Kind);

        // "...235959" is "until midnight": the next block starts exactly where it ends, so the guide has no gap.
        Assert.Equal(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), programs[0].EndDate);
        for (var i = 1; i < programs.Count; i++)
        {
            Assert.True(programs[i].StartDate >= programs[i - 1].EndDate, $"program {i} overlaps the one before");
        }
    }

    [Fact]
    public void Clock_time_titles_are_replaced_by_the_channel_name()
    {
        // The captured channel titles every block with its own start time ("22:00"), which says nothing in a guide.
        var programs = EpgMapper.Map(Fixture(), "cx_nba", TimeZoneInfo.Utc, Ever, Never);

        Assert.All(programs, p => Assert.Equal("NBA Eventos", p.Name));
    }

    [Fact]
    public void Real_titles_are_kept()
    {
        var programs = EpgMapper.Map(Payload(("20260915200000", "20260915220000", "Noticias Caracol")), "ch", TimeZoneInfo.Utc, Ever, Never);

        Assert.Equal("Noticias Caracol", Assert.Single(programs).Name);
    }

    [Fact]
    public void Times_are_read_in_the_configured_zone_and_returned_in_utc()
    {
        var bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"); // UTC-5, no DST

        var program = Assert.Single(EpgMapper.Map(Payload(("20260915200000", "20260915220000", "Show")), "ch", bogota, Ever, Never));

        Assert.Equal(new DateTime(2026, 9, 16, 1, 0, 0, DateTimeKind.Utc), program.StartDate);
        Assert.Equal(new DateTime(2026, 9, 16, 3, 0, 0, DateTimeKind.Utc), program.EndDate);
    }

    [Fact]
    public void Only_programs_overlapping_the_requested_window_are_returned()
    {
        var payload = Payload(
            ("20260915100000", "20260915120000", "Before"),
            ("20260915120000", "20260915140000", "Straddles start"),
            ("20260915140000", "20260915160000", "Inside"),
            ("20260915160000", "20260915180000", "Starts at end"));

        var programs = EpgMapper.Map(
            payload,
            "ch",
            TimeZoneInfo.Utc,
            new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 15, 16, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new[] { "Straddles start", "Inside" }, programs.Select(p => p.Name));
    }

    [Theory]
    [InlineData("not-a-time", "20260915120000")]
    [InlineData("20260915120000", "")]
    [InlineData("20260915140000", "20260915120000")]
    public void Unreadable_or_backwards_entries_are_dropped_without_failing_the_rest(string start, string end)
    {
        var payload = Payload((start, end, "Broken"), ("20260915200000", "20260915220000", "Fine"));

        Assert.Equal("Fine", Assert.Single(EpgMapper.Map(payload, "ch", TimeZoneInfo.Utc, Ever, Never)).Name);
    }

    [Fact]
    public void A_payload_without_a_program_list_is_an_empty_guide()
    {
        Assert.Empty(EpgMapper.Map(new JsonObject { ["name"] = "x" }, "ch", TimeZoneInfo.Utc, Ever, Never));
    }

    [Fact]
    public void Blank_zone_means_the_servers_own()
    {
        Assert.Same(TimeZoneInfo.Local, EpgMapper.ResolveZone(" ", out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void A_known_zone_is_used()
    {
        Assert.Equal("America/Bogota", EpgMapper.ResolveZone("America/Bogota", out var problem).Id);
        Assert.Null(problem);
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_the_servers_and_says_so()
    {
        var zone = EpgMapper.ResolveZone("Mars/Olympus_Mons", out var problem);

        Assert.Same(TimeZoneInfo.Local, zone);
        Assert.Contains("Mars/Olympus_Mons", problem);
    }
}
