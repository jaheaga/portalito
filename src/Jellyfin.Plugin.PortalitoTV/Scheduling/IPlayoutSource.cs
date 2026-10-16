namespace Jellyfin.Plugin.PortalitoTV.Scheduling;

/// <summary>A published channel.</summary>
/// <param name="Number">Channel number.</param>
/// <param name="Name">Channel name.</param>
/// <param name="LogoUrl">Channel logo.</param>
public sealed record ChannelDef(string Number, string Name, string? LogoUrl = null)
{
    /// <summary>Gets the channel id in the M3U list and the XMLTV guide.</summary>
    public string TvgId => "portalitotv." + Number;
}

/// <summary>Where the channels and their schedule come from (the stream and the guide read the same thing).</summary>
public interface IPlayoutSource
{
    /// <summary>Gets the published channels.</summary>
    IReadOnlyList<ChannelDef> Channels { get; }

    /// <summary>The programs of a channel that overlap the given period.</summary>
    /// <param name="channel">Channel number.</param>
    /// <param name="fromUtc">Start of the period.</param>
    /// <param name="toUtc">End of the period.</param>
    /// <returns>The programs in order.</returns>
    IEnumerable<PlayoutItem> Between(string channel, DateTime fromUtc, DateTime toUtc);

    /// <summary>The program on air at a given moment.</summary>
    /// <param name="channel">Channel number.</param>
    /// <param name="utc">The moment.</param>
    /// <returns>The program, or null if the schedule doesn't reach that far.</returns>
    PlayoutItem? At(string channel, DateTime utc);
}
