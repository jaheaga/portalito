using Jellyfin.Plugin.PortalitoTV.Media;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PortalitoTV.Configuration;

/// <summary>Plugin settings. Edited from the plugin's page, not by hand.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Jellyfin user whose favorites (hearts) define the available series and movies. Empty (the default)
    /// means the first administrator.
    /// </summary>
    public string FavoritesUser { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the IANA time zone block start times are read in ("20:00" = local time there). Empty (the default)
    /// means the server's own time zone; an unknown id falls back to UTC.
    /// </summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address of this Jellyfin as ffmpeg and the Live TV tuner reach it (used to build the stream, M3U and
    /// XMLTV URLs; no trailing slash).
    /// </summary>
    public string ServerUrl { get; set; } = "http://127.0.0.1:8096";

    /// <summary>Gets or sets the VAAPI render device the channels are encoded on.</summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";

    /// <summary>
    /// Gets or sets the key of the stream and guide URLs (Jellyfin's own tuner calls them without a user session). Generated
    /// on first start.
    /// </summary>
    public string StreamKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB v4 read token, for episode names, synopses and images in the guide.</summary>
    public string TmdbToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the per-series settings (the series themselves come from the favorites; this stores what the user chose).</summary>
    public List<SeriesSettings> Series { get; set; } = new();

    /// <summary>Gets or sets the channels.</summary>
    public List<ChannelSettings> Channels { get; set; } = new();

    /// <summary>Gets or sets the per-movie settings (only the movies the user changed).</summary>
    public List<MovieSettings> Movies { get; set; } = new();
}

/// <summary>What the user chose for a favorite series.</summary>
public class SeriesSettings
{
    /// <summary>Gets or sets the Portalito id (the one in the "[portalito-XXXX]" folder of the Siguiendo library).</summary>
    public string PortalitoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the name the last time it was seen (so it still shows after its heart is removed).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the series is used in channels ("Usar en canales").</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the unchecked seasons. The excluded ones are stored so a new season enters by itself.</summary>
    public List<int> ExcludedSeasons { get; set; } = new();

    /// <summary>Gets or sets the audio/subtitle choice.</summary>
    public EmitLanguage Language { get; set; } = EmitLanguage.Auto;

    /// <summary>
    /// Gets or sets the Portalito seasons that don't match TMDB and that the user assigned by hand to a TMDB season (in the
    /// numbering chosen for the series). Episodes are paired by episode number inside that season.
    /// </summary>
    public List<SeasonMapEntry> SeasonMap { get; set; } = new();

    /// <summary>Gets or sets when it first appeared (the "Nueva" label on the page).</summary>
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A Portalito season → TMDB season pair (assigned by hand).</summary>
public class SeasonMapEntry
{
    /// <summary>Gets or sets the season number in Portalito.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the season number in TMDB (in the numbering chosen for the series).</summary>
    public int TmdbSeason { get; set; }
}

/// <summary>What the user chose for a favorite movie.</summary>
public class MovieSettings
{
    /// <summary>Gets or sets the Jellyfin id ("N" format, no dashes).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the name the last time it was seen.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the audio/subtitle choice.</summary>
    public EmitLanguage Language { get; set; } = EmitLanguage.Auto;
}

/// <summary>How a channel is programmed.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ChannelMode
{
    /// <summary>Blocks at fixed times; each block fills up to the next one.</summary>
    Schedule,

    /// <summary>Continuous random mix.</summary>
    Shuffle,
}

/// <summary>Order inside a schedule block.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum BlockOrder
{
    /// <summary>Random, going through every episode before repeating.</summary>
    Shuffle,

    /// <summary>In broadcast order, resuming where the last block left off.</summary>
    Chronological,
}

/// <summary>A 24/7 channel.</summary>
public class ChannelSettings
{
    /// <summary>Gets or sets the channel number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the channel is published.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the channel logo.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Gets or sets how the channel is programmed.</summary>
    public ChannelMode Mode { get; set; } = ChannelMode.Schedule;

    /// <summary>Gets or sets the day's blocks (Schedule mode).</summary>
    public List<BlockSettings> Blocks { get; set; } = new();

    /// <summary>Gets or sets the series that cover the gaps (Schedule) or make up the channel (Shuffle).</summary>
    public List<string> FillerSeries { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether favorite movies are included (Shuffle mode).</summary>
    public bool IncludeFavoriteMovies { get; set; }
}

/// <summary>A block of a schedule channel's day.</summary>
public class BlockSettings
{
    /// <summary>Gets or sets the local start time, "HH:mm".</summary>
    public string Start { get; set; } = "00:00";

    /// <summary>
    /// Gets or sets the optional local end time, "HH:mm". Empty = up to the next block. If it is earlier than the start, it
    /// ends the next day. Filler goes between its end and the next block.
    /// </summary>
    public string? End { get; set; }

    /// <summary>Gets or sets the series' PortalitoId (or "movies" / "movie:&lt;id&gt;").</summary>
    public string SeriesId { get; set; } = string.Empty;

    /// <summary>Gets or sets the order of the episodes.</summary>
    public BlockOrder Order { get; set; } = BlockOrder.Shuffle;

    /// <summary>Gets or sets the days it applies (0 = Sunday ... 6 = Saturday). Empty = every day.</summary>
    public List<int> Days { get; set; } = new();
}
