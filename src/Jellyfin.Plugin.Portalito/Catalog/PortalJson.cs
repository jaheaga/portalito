using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>Tolerant readers for portal payloads, where ids and counters arrive as either JSON strings or numbers.</summary>
internal static class PortalJson
{
    // The portal's keyWords field carries an IMDb id ("tt1234567"), sometimes alone, sometimes in a list or a
    // longer phrase, sometimes as a JSON array. TMDB's find endpoint wants exactly the "tt…" token.
    private static readonly Regex ImdbPattern = new(@"tt\d{7,}", RegexOptions.Compiled);
    public static string? Str(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        };
    }

    public static int? Int(JsonNode? node)
        => int.TryParse(Str(node), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    public static IEnumerable<JsonObject> Objects(JsonNode? node)
        => node is JsonArray array ? array.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();

    /// <summary>Returns the objects of the first of <paramref name="keys"/> that holds an array (the reference client's fallback chain).</summary>
    public static IReadOnlyList<JsonObject> FirstList(JsonObject payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload[key] is JsonArray array)
            {
                return array.OfType<JsonObject>().ToArray();
            }
        }

        return Array.Empty<JsonObject>();
    }

    /// <summary>
    /// A content item's poster (portrait) URL, or null if it has none. Captured live from a real portal response
    /// 2026-09-22: each entry's own <c>posterList</c> carries a <c>fileType</c>/<c>fileUrl</c> pair per image --
    /// <c>"icon"</c> is the portrait poster a browse grid wants (discarding <c>"stage"</c>, a 100x100 thumbnail
    /// with no browse use, and treating <c>"poster"</c> as a landscape backdrop instead, which
    /// <see cref="MediaBrowser.Controller.Channels.ChannelItemInfo"/>'s single <c>ImageUrl</c> has no separate
    /// slot for).
    /// </summary>
    public static string? PosterUrl(JsonObject content)
        => Objects(content["posterList"])
            .FirstOrDefault(p => Str(p["fileType"]) == "icon") is { } poster
            ? Str(poster["fileUrl"])
            : null;

    /// <summary>A content item's landscape image (the <c>posterList</c> entry of <c>fileType</c> <c>"poster"</c>), or null.</summary>
    public static string? BackdropUrl(JsonObject content)
        => Objects(content["posterList"])
            .FirstOrDefault(p => Str(p["fileType"]) == "poster") is { } backdrop
            ? NonBlank(backdrop["fileUrl"])
            : null;

    /// <summary>Returns a non-blank string value, or null (never empty) -- so callers can null-coalesce cleanly.</summary>
    public static string? NonBlank(JsonNode? node) => Str(node) is { Length: > 0 } s ? s : null;

    /// <summary>The year from a <c>releaseTime</c> field ("2026-07-08" or "2026"); null if not a 4-digit year.</summary>
    public static int? Year(JsonNode? node)
        => Str(node) is { Length: >= 4 } s && int.TryParse(s.AsSpan(0, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? y
            : null;

    /// <summary>Splits a comma-separated field ("Action,Adventure,Comedy") into trimmed, non-empty parts.</summary>
    public static IReadOnlyList<string> CommaList(JsonNode? node)
        => Str(node)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

    /// <summary>
    /// The first IMDb id ("tt1234567") in a <c>keyWords</c> field, or null. Accepts a plain string, a comma/space
    /// list, a longer phrase, or a JSON array of any of those. Only the exact <c>tt</c>+digits token is returned, so
    /// it is safe to drop straight into a TMDB <c>find</c> URL.
    /// </summary>
    public static string? ImdbId(JsonNode? node)
    {
        var candidates = node is JsonArray array ? array.Select(Str) : new[] { Str(node) };
        foreach (var candidate in candidates)
        {
            if (candidate is not null && ImdbPattern.Match(candidate) is { Success: true } match)
            {
                return match.Value;
            }
        }

        return null;
    }
}
