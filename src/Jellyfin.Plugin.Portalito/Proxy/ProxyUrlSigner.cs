using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Portalito.Proxy;

/// <summary>
/// Creates and verifies the signed URLs of the plugin's anonymous proxy endpoints. ffmpeg cannot send a Jellyfin API
/// key, so the endpoints are <c>[AllowAnonymous]</c> and each URL carries an expiry and an HMAC over its parameters;
/// without this, anyone on the LAN could use Jellyfin as an open relay for the portal account.
/// </summary>
public sealed class ProxyUrlSigner
{
    private readonly byte[] _secret;
    private readonly string _baseUrl;
    private readonly TimeProvider _clock;

    public ProxyUrlSigner(string secret, string baseUrl, TimeProvider clock)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("The proxy signing secret is empty.", nameof(secret));
        }

        _secret = Encoding.UTF8.GetBytes(secret);
        _baseUrl = baseUrl.TrimEnd('/');
        _clock = clock;
    }

    /// <summary>Gets the base URL every proxy URL starts with (what ffmpeg must be able to reach).</summary>
    public string BaseUrl => _baseUrl;

    public string LivePlaylistUrl(string channel, TimeSpan validFor)
    {
        var e = ExpiryFor(validFor);
        return $"{_baseUrl}/Portalito/live/{Uri.EscapeDataString(channel)}.m3u8?e={e}&s={Sign("live", e, channel)}";
    }

    public string SegmentUrl(string channel, string upstreamUrl, TimeSpan validFor)
    {
        var e = ExpiryFor(validFor);
        return $"{_baseUrl}/Portalito/seg.ts?c={Uri.EscapeDataString(channel)}&u={Uri.EscapeDataString(upstreamUrl)}"
               + $"&e={e}&s={Sign("seg", e, channel, upstreamUrl)}";
    }

    public string VodUrl(string contentId, string seriesId, TimeSpan validFor)
    {
        var e = ExpiryFor(validFor);
        var series = string.IsNullOrEmpty(seriesId) ? string.Empty : $"series={Uri.EscapeDataString(seriesId)}&";
        return $"{_baseUrl}/Portalito/vod/{Uri.EscapeDataString(contentId)}?{series}e={e}&s={Sign("vod", e, contentId, seriesId)}";
    }

    /// <summary>
    /// A VOD URL that never expires, for the "Siguiendo" library's <c>.strm</c> files: Jellyfin reads a .strm's URL when it
    /// plays the episode, maybe months after the file was written, so it can't carry an expiry. It still carries an HMAC
    /// over its ids (a different kind than <see cref="VodUrl"/>, so neither can stand in for the other), and rotating
    /// the signing secret revokes every one of them -- the next sync rewrites the files.
    /// </summary>
    public string PlayUrl(string contentId, string seriesId)
    {
        var series = string.IsNullOrEmpty(seriesId) ? string.Empty : $"series={Uri.EscapeDataString(seriesId)}&";
        return $"{_baseUrl}/Portalito/play/{Uri.EscapeDataString(contentId)}?{series}s={Sign("play", 0, contentId, seriesId)}";
    }

    public bool VerifyPlay(string contentId, string seriesId, string? signature)
        => VerifyMac(signature, "play", 0, contentId, seriesId);

    /// <summary>
    /// A poster URL routed through the plugin's own proxy instead of hotlinked straight to Portalito's CDN: the CDN
    /// serves images as <c>Content-Type: image/jpg</c> (non-standard -- confirmed live 2026-09-23), and Jellyfin's
    /// own image cache/converter throws ("Unable to determine image file extension from mime type image/jpg") on
    /// every request for it, so a hotlinked poster silently 400s no matter what a client asks for. The <c>img</c>
    /// endpoint re-serves the same bytes with the extension-recognisable <c>image/jpeg</c> instead.
    /// </summary>
    public string ImageUrl(string upstreamUrl, TimeSpan validFor)
    {
        var e = ExpiryFor(validFor);
        return $"{_baseUrl}/Portalito/img?u={Uri.EscapeDataString(upstreamUrl)}&e={e}&s={Sign("img", e, upstreamUrl)}";
    }

    /// <summary>
    /// A URL that answers <paramref name="nonce"/> back when it reaches this plugin: the connection test uses it to
    /// prove the proxy base URL leads to THIS server's Portalito endpoints (not a 404, and not some other server).
    /// </summary>
    public string PingUrl(string nonce, TimeSpan validFor)
    {
        var e = ExpiryFor(validFor);
        return $"{_baseUrl}/Portalito/ping?n={Uri.EscapeDataString(nonce)}&e={e}&s={Sign("ping", e, nonce)}";
    }

    public bool VerifyPing(string nonce, long expiry, string? signature)
        => Verify(signature, expiry, "ping", nonce);

    public bool VerifyLivePlaylist(string channel, long expiry, string? signature)
        => Verify(signature, expiry, "live", channel);

    public bool VerifySegment(string channel, string upstreamUrl, long expiry, string? signature)
        => Verify(signature, expiry, "seg", channel, upstreamUrl);

    public bool VerifyVod(string contentId, string seriesId, long expiry, string? signature)
        => Verify(signature, expiry, "vod", contentId, seriesId);

    public bool VerifyImage(string upstreamUrl, long expiry, string? signature)
        => Verify(signature, expiry, "img", upstreamUrl);

    private long ExpiryFor(TimeSpan validFor) => _clock.GetUtcNow().Add(validFor).ToUnixTimeSeconds();

    private bool Verify(string? signature, long expiry, string kind, params string[] parts)
    {
        return expiry >= _clock.GetUtcNow().ToUnixTimeSeconds() && VerifyMac(signature, kind, expiry, parts);
    }

    private bool VerifyMac(string? signature, string kind, long expiry, params string[] parts)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(provided, Mac(kind, expiry, parts));
    }

    private string Sign(string kind, long expiry, params string[] parts)
        => Convert.ToHexString(Mac(kind, expiry, parts)).ToLowerInvariant();

    // Each part is length-prefixed so different splits of the same characters cannot produce the same payload.
    private byte[] Mac(string kind, long expiry, string[] parts)
    {
        var payload = new StringBuilder(kind).Append('\n').Append(expiry.ToString(CultureInfo.InvariantCulture));
        foreach (var part in parts)
        {
            payload.Append('\n').Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
        }

        return HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload.ToString()));
    }
}
