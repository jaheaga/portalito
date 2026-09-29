using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Portalito.Proxy;

/// <summary>
/// Rewrites the CDN's live playlist so everything the player fetches from it goes back through the plugin's signing
/// proxy: the CDN refuses any request without Content-Auth/Content-License, so one URL left pointing at it is one
/// request ffmpeg makes without them.
/// </summary>
public static partial class HlsRewriter
{
    /// <summary>
    /// Replaces each media playlist URI with <paramref name="mapUri"/>'s result: every segment line, and the
    /// <c>URI="..."</c> of <c>#EXT-X-KEY</c> / <c>#EXT-X-MAP</c>. Relative URIs are resolved against
    /// <paramref name="baseUri"/> (the playlist's own URL) first. Other tags (including the CDN's custom
    /// <c>#EXT-SEGMENT</c> tags) pass through unchanged, as does any URI that isn't http(s) once resolved.
    /// </summary>
    public static string Rewrite(string playlist, Uri? baseUri, Func<string, string> mapUri)
    {
        var output = new StringBuilder(playlist.Length + 256);
        foreach (var line in SplitLines(playlist))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                output.Append(line);
            }
            else if (trimmed[0] != '#')
            {
                output.Append(Resolve(trimmed, baseUri) is { } uri ? mapUri(uri) : line);
            }
            else if (trimmed.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) || trimmed.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                output.Append(UriAttributeRegex().Replace(
                    line,
                    m => Resolve(m.Groups[1].Value, baseUri) is { } uri ? $"URI=\"{mapUri(uri)}\"" : m.Value));
            }
            else
            {
                output.Append(line);
            }

            output.Append('\n');
        }

        return output.ToString();
    }

    /// <summary>
    /// For a master playlist (one listing <c>#EXT-X-STREAM-INF</c> variants instead of segments), the URI of its
    /// highest-bandwidth variant, resolved against <paramref name="baseUri"/>; null for a media playlist.
    /// </summary>
    public static string? BestVariant(string playlist, Uri? baseUri)
    {
        string? best = null;
        var bestBandwidth = -1L;
        long? pending = null;
        foreach (var raw in SplitLines(playlist))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                var m = BandwidthRegex().Match(line);
                pending = m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bw) ? bw : 0;
            }
            else if (pending is { } bandwidth && line.Length > 0 && line[0] != '#')
            {
                if (bandwidth > bestBandwidth && Resolve(line, baseUri) is { } uri)
                {
                    best = uri;
                    bestBandwidth = bandwidth;
                }

                pending = null;
            }
        }

        return best;
    }

    private static string? Resolve(string reference, Uri? baseUri)
    {
        Uri? uri;
        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            uri = absolute;
        }
        else if (baseUri is not null && !reference.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(baseUri, reference, out var resolved))
        {
            uri = resolved;
        }
        else
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || text[i] == '\r')
            {
                yield return text[start..i];
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    [GeneratedRegex("URI=\"([^\"]*)\"")]
    private static partial Regex UriAttributeRegex();

    // (?<![-A-Z]) so AVERAGE-BANDWIDTH isn't read as BANDWIDTH.
    [GeneratedRegex("(?<![-A-Z])BANDWIDTH=(\\d+)")]
    private static partial Regex BandwidthRegex();
}
