using Jellyfin.Plugin.PortalitoTV.Media;

namespace Jellyfin.Plugin.PortalitoTV.Scheduling;

/// <summary>A program of the grid: which Jellyfin item airs, when and in which language.</summary>
/// <param name="ItemId">Jellyfin episode or movie (<see cref="Guid.Empty"/> = black slate).</param>
/// <param name="StartUtc">Start in the grid.</param>
/// <param name="EndUtc">End in the grid (may cut the item, e.g. filler).</param>
/// <param name="InPoint">Where in the item it starts (0 except for cuts).</param>
/// <param name="Language">Requested language; the actual track is chosen when airing.</param>
/// <param name="Title">Series or movie title.</param>
/// <param name="EpisodeTitle">Episode title.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="Overview">Synopsis.</param>
/// <param name="ImageUrl">Guide image.</param>
/// <param name="Year">Year.</param>
/// <param name="IsMovie">Whether it is a movie.</param>
/// <param name="CollectionKey">Collection it came from (to give it back if the schedule is rebuilt).</param>
/// <param name="IsFiller">Filler (cut so the next block starts on time).</param>
/// <param name="PartGroup">Multi-part episode ("first part", "(1)"...): id of the group; the parts air together and in order. Null = a single episode.</param>
/// <param name="PartIndex">Part number inside the group (1 = first); 0 if it is not multi-part.</param>
/// <param name="PartCount">How many parts the group has (the available ones); 0 if it is not multi-part.</param>
public sealed record PlayoutItem(
    Guid ItemId,
    DateTime StartUtc,
    DateTime EndUtc,
    TimeSpan InPoint,
    EmitLanguage Language,
    string Title,
    string? EpisodeTitle = null,
    int? Season = null,
    int? Episode = null,
    string? Overview = null,
    string? ImageUrl = null,
    int? Year = null,
    bool IsMovie = false,
    string? CollectionKey = null,
    bool IsFiller = false,
    string? PartGroup = null,
    int PartIndex = 0,
    int PartCount = 0)
{
    /// <summary>Gets how long the program lasts in the grid.</summary>
    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>Gets a value indicating whether this is the black slate.</summary>
    public bool IsSlate => ItemId == Guid.Empty;
}
