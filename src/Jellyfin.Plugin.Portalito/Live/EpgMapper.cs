using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Portalito.Catalog;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.Portalito.Live;

/// <summary>
/// Maps <c>v3/getProgram</c> onto Jellyfin's guide. Shape from a live capture (2026-09-22,
/// <c>reference/sample_getProgram_response.json</c>): <c>programList[]</c> = <c>{contentId, programName, startTime,
/// endTime, type}</c>, times as <c>yyyyMMddHHmmss</c> with no zone -- which zone they are in is a setting
/// (<see cref="Configuration.PluginConfiguration.EpgTimeZone"/>), since the portal never says.
/// </summary>
public static partial class EpgMapper
{
    private const string TimeFormat = "yyyyMMddHHmmss";

    /// <summary>
    /// The channel's programs that overlap [<paramref name="startUtc"/>, <paramref name="endUtc"/>), in start order.
    /// Entries with unreadable times, or that end before they start, are dropped rather than failing the whole guide.
    /// </summary>
    public static IReadOnlyList<ProgramInfo> Map(JsonObject payload, string channelId, TimeZoneInfo zone, DateTime startUtc, DateTime endUtc)
    {
        var channelName = PortalJson.NonBlank(payload["name"]) ?? channelId;
        var programs = new List<ProgramInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in PortalJson.Objects(payload["programList"]))
        {
            if (ToUtc(PortalJson.Str(entry["startTime"]), zone) is not { } start
                || ToUtc(PortalJson.Str(entry["endTime"]), zone) is not { } rawEnd)
            {
                continue;
            }

            // "...235959" means "until midnight": without the missing second the guide shows a 1s gap between blocks.
            var end = rawEnd.Second == 59 ? rawEnd.AddSeconds(1) : rawEnd;
            if (end <= start || end <= startUtc || start >= endUtc)
            {
                continue;
            }

            var contentId = PortalJson.NonBlank(entry["contentId"]);
            var id = $"{channelId}_{contentId ?? start.ToString(TimeFormat, CultureInfo.InvariantCulture)}";
            if (!seen.Add(id))
            {
                continue;
            }

            programs.Add(new ProgramInfo
            {
                Id = id,
                ChannelId = channelId,
                Name = Title(PortalJson.NonBlank(entry["programName"]), channelName),
                StartDate = start,
                EndDate = end,
            });
        }

        return programs.OrderBy(p => p.StartDate).ToList();
    }

    /// <summary>
    /// The zone named by <paramref name="id"/> (an IANA id like <c>America/Bogota</c>, or a Windows id), or the
    /// server's own when blank. An unknown id falls back to the server's zone, with <paramref name="problem"/> saying
    /// so -- a typo in an optional guide setting must not take the whole plugin down.
    /// </summary>
    public static TimeZoneInfo ResolveZone(string? id, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            problem = $"Time zone '{id.Trim()}' is unknown on this server; guide times are read in the server's own zone ({TimeZoneInfo.Local.Id}) instead.";
            return TimeZoneInfo.Local;
        }
    }

    // Some channels (the captured NBA one) publish filler blocks titled with their own start time ("22:00"). A guide
    // full of clock times says nothing; the channel's name at least says what's on.
    private static string Title(string? programName, string channelName)
        => programName is null || ClockTimeRegex().IsMatch(programName) ? channelName : programName;

    private static DateTime? ToUtc(string? text, TimeZoneInfo zone)
    {
        if (text is null || !DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
        }
        catch (ArgumentException)
        {
            // A wall-clock time that doesn't exist in that zone (the hour skipped by a DST change).
            return null;
        }
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2}$")]
    private static partial Regex ClockTimeRegex();
}
