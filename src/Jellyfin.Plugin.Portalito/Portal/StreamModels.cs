namespace Jellyfin.Plugin.Portalito.Portal;

/// <summary>One live CDN candidate: its host, the base Content-Auth query string, and the Content-Auth token inside it.</summary>
public sealed record LiveCdn(string Host, string AuthUrl, string Token);

/// <summary>Everything the proxy needs to fetch and sign a live channel through the CDN's "cfl" (Cloudflare) entry.</summary>
/// <param name="ChannelCode">Portal channel code.</param>
/// <param name="CflHost">CDN host (rotates; always re-read from get_slb).</param>
/// <param name="CflAuthUrl">The base Content-Auth query string from get_slb (no sign2 yet).</param>
/// <param name="License">The Content-License value from play_live.</param>
/// <param name="Token">The 32-hex token inside <paramref name="CflAuthUrl"/>, the input to Content-Auth.</param>
/// <param name="ExpiresAt">When the auth URL expires, if it says.</param>
/// <param name="PlayCode">
/// What the signal is called on the CDN, from the same <c>liveAddressList</c> entry as <paramref name="License"/>.
/// It isn't always the channel code (the reference client measured <c>cx-EXAMPLE</c> served as
/// <c>cx-2EF7E10E40C1ac19D6A9F3ED4CD2</c>), and asking the CDN for the channel code with this license is asking for
/// a signal the license doesn't authorize: 401. Empty when the portal sends none, and then the channel code works.
/// </param>
public sealed record LiveStream(
    string ChannelCode,
    string CflHost,
    string CflAuthUrl,
    string License,
    string Token,
    DateTimeOffset? ExpiresAt,
    string? PlayCode = null,
    IReadOnlyList<LiveCdn>? Alternates = null)
{
    /// <summary>Gets the name to request from the CDN: <see cref="PlayCode"/>, else the channel code.</summary>
    public string CdnCode => string.IsNullOrEmpty(PlayCode) ? ChannelCode : PlayCode;

    /// <summary>
    /// Every cfl CDN the portal returned, the primary first (its host/auth/token), then the <see cref="Alternates"/>.
    /// The proxy tries them in order so a channel whose first CDN is down or serving an error page still plays. When
    /// the portal returns only one, this is a single-element list and behavior is exactly as before.
    /// </summary>
    public IReadOnlyList<LiveCdn> Cdns =>
        new[] { new LiveCdn(CflHost, CflAuthUrl, Token) }.Concat(Alternates ?? Array.Empty<LiveCdn>()).ToList();
}

/// <summary>Everything the proxy needs to fetch a VOD file from the CDN.</summary>
/// <param name="MediaCode">Media code from play_vod (names the file on the CDN).</param>
/// <param name="Host">CDN host[:port].</param>
/// <param name="CflAuthUrl">The Content-Auth value from get_slb (VOD needs no per-request sign2).</param>
/// <param name="License">The Content-License value from play_vod.</param>
/// <param name="ExpiresAt">When the auth URL expires, if it says.</param>
/// <param name="VideoFormat">
/// The movie's <c>videoFormat</c> from play_vod (e.g. "ts", "mp4"). Determines the CDN file's real extension --
/// requesting the wrong one serves a different, invalid file for that title (measured live 2026-09-21: ffmpeg got
/// "moov atom not found" opening a title whose real format was "ts" as "_media.mp4"). Reference rule
/// (the reference client): ext = "ts" if video_format == "ts" else "mp4" -- "ts" is the only value that changes
/// the extension; everything else (including missing/unknown) falls back to mp4.
/// </param>
/// <param name="Subtitles">External subtitle files play_vod lists for this title (<c>episodeList[0].subtitleList</c>).</param>
public sealed record VodStream(
    string MediaCode,
    string Host,
    string CflAuthUrl,
    string License,
    DateTimeOffset? ExpiresAt,
    string? VideoFormat = null,
    IReadOnlyList<SubtitleFile>? Subtitles = null)
{
    public Uri MediaUri => new($"http://{Host}/vod/{MediaCode}_media.{(VideoFormat == "ts" ? "ts" : "mp4")}");
}

/// <summary>One external subtitle file from play_vod: plain HTTP, no auth headers needed (the reference client).</summary>
/// <param name="Language">The portal's own language value, as sent (e.g. "es").</param>
/// <param name="Url">The file's URL -- or, once <c>SubtitleFileCache</c> has stored it, its local path.</param>
/// <param name="Format">"srt" or "vtt".</param>
public sealed record SubtitleFile(string Language, string Url, string Format);
