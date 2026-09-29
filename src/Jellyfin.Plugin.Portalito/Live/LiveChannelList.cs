using System.Text.Json.Nodes;
using Jellyfin.Plugin.Portalito.Catalog;

namespace Jellyfin.Plugin.Portalito.Live;

/// <summary>One live channel from <c>v6/getLiveData</c>'s <c>channelList</c>.</summary>
public sealed record LiveChannelEntry(string Code, string Name, string? Number, string? PosterUrl);

/// <summary>Parses the live channel list shared by the Live TV service and the VOD channel's "TV en vivo" folder.</summary>
public static class LiveChannelList
{
    // Live channel list items carry a plain posterUrl field, not the posterList array VOD content uses.
    public static IReadOnlyList<LiveChannelEntry> Parse(JsonObject payload)
    {
        var channels = new List<LiveChannelEntry>();
        foreach (var channel in PortalJson.Objects(payload["channelList"]))
        {
            var code = PortalJson.Str(channel["channelCode"]);
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            var name = PortalJson.Str(channel["name"]);
            channels.Add(new LiveChannelEntry(
                code,
                string.IsNullOrWhiteSpace(name) ? code : name,
                PortalJson.Str(channel["channelNumber"]),
                PortalJson.NonBlank(channel["posterUrl"])));
        }

        return channels;
    }

    /// <summary>
    /// Whether a channel looks HD from its name ("CARACOL COL HD", "ESPN FHD") or its code's resolution suffix
    /// (<c>cx_…_720p</c>); null when neither says, rather than guessing "not HD".
    /// </summary>
    public static bool? LooksHd(LiveChannelEntry channel)
    {
        var words = channel.Name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => HdWords.Contains(w)))
        {
            return true;
        }

        return channel.Code.EndsWith("_720p", StringComparison.OrdinalIgnoreCase)
            || channel.Code.EndsWith("_1080p", StringComparison.OrdinalIgnoreCase)
            || channel.Code.EndsWith("_2160p", StringComparison.OrdinalIgnoreCase)
            ? true
            : null;
    }

    private static readonly char[] NameSeparators = { ' ', '-', '_', '(', ')', '[', ']', '|', '.' };

    private static readonly HashSet<string> HdWords = new(StringComparer.OrdinalIgnoreCase) { "HD", "FHD", "UHD", "4K", "720p", "1080p", "2160p" };
}
