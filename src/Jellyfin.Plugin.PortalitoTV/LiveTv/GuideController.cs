using System.Globalization;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.PortalitoTV.Metadata;
using Jellyfin.Plugin.PortalitoTV.Scheduling;
using Jellyfin.Plugin.PortalitoTV.Streaming;
using Jellyfin.Plugin.PortalitoTV.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PortalitoTV.LiveTv;

/// <summary>
/// The channels' M3U list and XMLTV guide. Jellyfin's M3U tuner and XMLTV provider consume them (registered automatically by
/// <see cref="JellyfinRegistration"/>). M3U is used instead of <c>ILiveTvService</c> because Jellyfin marks every stream of a
/// plugin service as interlaced and re-encodes it with yadif on the CPU.
/// </summary>
[ApiController]
[Route("PortalitoTV")]
public sealed class GuideController : ControllerBase
{
    /// <summary>How far back the guide publishes.</summary>
    private static readonly TimeSpan Past = TimeSpan.FromHours(2);

    /// <summary>How far ahead the guide publishes.</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(48);

    private readonly StreamToken _token;
    private readonly IPlayoutSource _playout;

    /// <summary>Initializes a new instance of the <see cref="GuideController"/> class.</summary>
    /// <param name="token">The stream key.</param>
    /// <param name="playout">The channels' schedule.</param>
    public GuideController(StreamToken token, IPlayoutSource playout)
    {
        _token = token;
        _playout = playout;
    }

    /// <summary>The URL of the M3U list.</summary>
    /// <param name="token">The stream key.</param>
    /// <returns>The URL (it points at this Jellyfin, see <see cref="ServerUrl"/>).</returns>
    public static string M3uUrl(string token) => $"{ServerUrl.Current}/PortalitoTV/channels.m3u?k={token}";

    /// <summary>The URL of the XMLTV guide.</summary>
    /// <param name="token">The stream key.</param>
    /// <returns>The URL (it points at this Jellyfin, see <see cref="ServerUrl"/>).</returns>
    public static string XmltvUrl(string token) => $"{ServerUrl.Current}/PortalitoTV/xmltv.xml?k={token}";

    /// <summary>Subtitle of a program in Live TV: "T3 E5 - Episode name" (season, episode and name).</summary>
    /// <remarks>
    /// Without a name it is "T3 E5"; without numbers, just the name. Movies and the black slate have no subtitle.
    /// </remarks>
    /// <param name="p">The program.</param>
    /// <returns>The subtitle, or null if the program has none.</returns>
    public static string? SubTitle(PlayoutItem p)
    {
        if (p.IsMovie || p.IsSlate)
        {
            return null;
        }

        // Placeholder names ("Episodio 3", "<Show> Season 27 - 1") say nothing: just the numbers.
        var name = TmdbClient.IsGenericTitle(p.EpisodeTitle) ? null : p.EpisodeTitle!.Trim();
        if (p.Season is { } s && p.Episode is { } e)
        {
            var numbers = string.Create(CultureInfo.InvariantCulture, $"T{s} E{e}");
            return name is null ? numbers : numbers + " - " + name;
        }

        return name;
    }

    /// <summary>The M3U list of the channels.</summary>
    /// <param name="k">The stream key.</param>
    /// <returns>The list.</returns>
    [HttpGet("channels.m3u")]
    public ActionResult Channels([FromQuery] string? k)
    {
        if (!_token.IsValid(k))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var ch in _playout.Channels)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"#EXTINF:-1 tvg-id=\"{ch.TvgId}\" tvg-chno=\"{ch.Number}\" tvg-name=\"{ch.Name}\" tvg-logo=\"{ch.LogoUrl}\" group-title=\"Portalito TV\",{ch.Name}\n");
            sb.Append(ChannelStreamController.LiveUrl(ch.Number, _token.Value)).Append('\n');
        }

        return Content(sb.ToString(), "audio/x-mpegurl", Encoding.UTF8);
    }

    /// <summary>The XMLTV guide of the channels.</summary>
    /// <param name="k">The stream key.</param>
    /// <returns>The guide.</returns>
    [HttpGet("xmltv.xml")]
    public ActionResult Xmltv([FromQuery] string? k)
    {
        if (!_token.IsValid(k))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var now = DateTime.UtcNow;
        var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("tv");
            w.WriteAttributeString("generator-info-name", "Portalito TV");
            foreach (var ch in _playout.Channels)
            {
                w.WriteStartElement("channel");
                w.WriteAttributeString("id", ch.TvgId);
                w.WriteElementString("display-name", ch.Name);
                w.WriteElementString("display-name", ch.Number);
                if (ch.LogoUrl is not null)
                {
                    w.WriteStartElement("icon");
                    w.WriteAttributeString("src", ch.LogoUrl);
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            foreach (var ch in _playout.Channels)
            {
                foreach (var p in _playout.Between(ch.Number, now - Past, now + Ahead))
                {
                    WriteProgramme(w, ch, p);
                }
            }

            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return File(ms.ToArray(), "application/xml");
    }

    private static void WriteProgramme(XmlWriter w, ChannelDef ch, PlayoutItem p)
    {
        w.WriteStartElement("programme");
        w.WriteAttributeString("start", XmltvTime(p.StartUtc));
        w.WriteAttributeString("stop", XmltvTime(p.EndUtc));
        w.WriteAttributeString("channel", ch.TvgId);
        Lang(w, "title", p.Title);
        if (SubTitle(p) is { } subTitle)
        {
            Lang(w, "sub-title", subTitle);
        }

        if (!string.IsNullOrEmpty(p.Overview))
        {
            Lang(w, "desc", p.Overview);
        }

        if (p.Year is { } year)
        {
            w.WriteElementString("date", year.ToString(CultureInfo.InvariantCulture));
        }

        Lang(w, "category", p.IsMovie ? "movie" : "series");
        if (p.ImageUrl is not null)
        {
            w.WriteStartElement("icon");
            w.WriteAttributeString("src", p.ImageUrl);
            w.WriteEndElement();
        }

        // No <episode-num> on purpose: Jellyfin turns it into season/episode numbers and the client puts "S3 - 6." in front of
        // the subtitle, which already carries "T3 E6 - name".
        w.WriteEndElement();
    }

    private static void Lang(XmlWriter w, string element, string text)
    {
        w.WriteStartElement(element);
        w.WriteAttributeString("lang", "es");
        w.WriteString(text);
        w.WriteEndElement();
    }

    private static string XmltvTime(DateTime utc) => utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
}
