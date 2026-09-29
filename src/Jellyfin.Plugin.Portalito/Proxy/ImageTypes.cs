namespace Jellyfin.Plugin.Portalito.Proxy;

/// <summary>
/// The image media types Jellyfin can save. Its image cache maps a response's type to a file extension and throws on
/// anything it doesn't know ("Unable to determine image file extension from mime type image/*"), and the portal's
/// CDNs send "image/jpg" and, for some posters, "image/*" (both measured live 2026-09-24).
/// </summary>
public static class ImageTypes
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "image/bmp",
    };

    /// <summary>The type as-is when Jellyfin knows it; null when only the bytes can tell (<see cref="Sniff"/>).</summary>
    public static string? Normalize(string? mediaType)
        => mediaType is null ? null
            : mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
            : Known.Contains(mediaType) ? mediaType.ToLowerInvariant()
            : null;

    /// <summary>The type from the file's first bytes; JPEG, the CDNs' usual format, when they don't say.</summary>
    public static string Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }))
        {
            return "image/png";
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (head.StartsWith("GIF8"u8))
        {
            return "image/gif";
        }

        if (head.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        return "image/jpeg";
    }
}
