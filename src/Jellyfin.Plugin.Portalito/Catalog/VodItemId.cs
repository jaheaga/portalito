using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Portalito.Catalog;

public enum VodItemKind
{
    /// <summary>A playable movie.</summary>
    Movie,

    /// <summary>A playable episode of a series.</summary>
    Episode,

    /// <summary>The single entry point into the Portalito catalog browser (a folder of <see cref="Catalog"/> folders).</summary>
    Discover,

    /// <summary>The "Destacado" root: a folder of curated <see cref="Row"/> folders (Estrenos, Mejor valoradas).</summary>
    Featured,

    /// <summary>
    /// One curated row. Primary = catalog index (into <c>CatalogBrowser.Catalogs</c>); Secondary = "estrenos" (the
    /// newest titles of the newest year) or "top" (the best-rated of the recent catalog).
    /// </summary>
    Row,

    /// <summary>One catalog (movies/_series/_kids/_anime). Primary = index into <c>CatalogBrowser.Catalogs</c>.</summary>
    Catalog,

    /// <summary>
    /// One filtered listing inside a catalog. Primary = catalog index; Secondary = "all" (no filter), "g&lt;tag
    /// index&gt;" (one genre), "y&lt;year&gt;" (one year), or "years" (a folder of year sub-folders).
    /// </summary>
    Filter,

    /// <summary>A show grouping one or more Portalito season items. Primary = the first season's contentId.</summary>
    Show,

    /// <summary>
    /// A season reached through a <see cref="Show"/> folder (a folder of episodes). Primary = the season's contentId.
    /// Its prefix is <c>ssn:</c>, not the <c>ser:</c> the removed native-shelf browsing used for the same content id:
    /// Jellyfin persists a channel folder's Name/IndexNumber keyed by the id's derived GUID, and its database still
    /// holds those old <c>ser:</c> folders with their raw portal titles -- reusing that prefix would bring back the
    /// collision confirmed live 2026-09-23 (a season showing "Avatar: The last airbender T2" instead of "Temporada 2").
    /// </summary>
    ShowSeason,

    /// <summary>The "TV en vivo" folder: one folder per live category the portal serves (countries, themes).</summary>
    LiveRoot,

    /// <summary>One of the portal's live categories (a <c>live</c> column). Primary = its columnId.</summary>
    LiveCategory,

    /// <summary>A live channel, played through the same signed HLS proxy as Jellyfin's Live TV. Primary = channelCode.</summary>
    LiveChannel,
}

/// <summary>
/// The id Jellyfin stores for a VOD channel item. Round-trips through a string so a folder id or a media-info request can
/// be turned back into the portal ids it stands for. Parts are restricted to a strict charset because they end up in
/// portal requests and proxy URLs.
/// </summary>
public readonly partial record struct VodItemId(VodItemKind Kind, string Primary, string? Secondary = null)
{
    public static VodItemId Movie(string contentId) => new(VodItemKind.Movie, contentId);

    public static VodItemId Episode(string seriesId, string episodeId) => new(VodItemKind.Episode, seriesId, episodeId);

    public static VodItemId Discover() => new(VodItemKind.Discover, "root");

    public static VodItemId Featured() => new(VodItemKind.Featured, "root");

    public static VodItemId Row(int catalogIndex, string mode) => new(VodItemKind.Row, catalogIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), mode);

    public static VodItemId Catalog(int catalogIndex) => new(VodItemKind.Catalog, catalogIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static VodItemId FilterAll(int catalogIndex) => new(VodItemKind.Filter, catalogIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), "all");

    public static VodItemId FilterGenre(int catalogIndex, int tagIndex) => new(
        VodItemKind.Filter,
        catalogIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "g" + tagIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static VodItemId FilterYear(int catalogIndex, int year) => new(
        VodItemKind.Filter,
        catalogIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "y" + year.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static VodItemId FilterYears(int catalogIndex) => new(VodItemKind.Filter, catalogIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), "years");

    public static VodItemId Show(string contentId) => new(VodItemKind.Show, contentId);

    public static VodItemId ShowSeason(string contentId) => new(VodItemKind.ShowSeason, contentId);

    public static VodItemId LiveRoot() => new(VodItemKind.LiveRoot, "root");

    public static VodItemId LiveCategory(long columnId) => new(VodItemKind.LiveCategory, columnId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static VodItemId LiveChannel(string channelCode) => new(VodItemKind.LiveChannel, channelCode);

    /// <summary>Gets a value indicating whether a portal id may be embedded in an item id (and later in a URL).</summary>
    public static bool IsSafePart(string? part) => part is not null && SafePart().IsMatch(part);

    public static bool TryParse(string? text, out VodItemId id)
    {
        id = default;
        var parts = (text ?? string.Empty).Split(':');
        if (parts.Length < 2 || parts[0].Length == 0 || !parts.Skip(1).All(IsSafePart))
        {
            return false;
        }

        switch (parts[0], parts.Length)
        {
            case ("mov", 2):
                id = Movie(parts[1]);
                return true;
            case ("epi", 3):
                id = Episode(parts[1], parts[2]);
                return true;
            case ("dis", 2):
                id = Discover();
                return true;
            case ("fea", 2):
                id = Featured();
                return true;
            case ("row", 3):
                if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    return false;
                }

                id = new VodItemId(VodItemKind.Row, parts[1], parts[2]);
                return true;
            case ("cat", 2):
                if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var catalogIndex))
                {
                    return false;
                }

                id = Catalog(catalogIndex);
                return true;
            case ("flt", 3):
                if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    return false;
                }

                id = new VodItemId(VodItemKind.Filter, parts[1], parts[2]);
                return true;
            case ("shw", 2):
                id = Show(parts[1]);
                return true;
            case ("ssn", 2):
                id = ShowSeason(parts[1]);
                return true;
            case ("liv", 2):
                id = LiveRoot();
                return true;
            case ("lcat", 2):
                if (!long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var columnId))
                {
                    return false;
                }

                id = LiveCategory(columnId);
                return true;
            case ("lch", 2):
                id = LiveChannel(parts[1]);
                return true;
            default:
                return false;
        }
    }

    public override string ToString() => Kind switch
    {
        VodItemKind.Movie => $"mov:{Primary}",
        VodItemKind.Episode => $"epi:{Primary}:{Secondary}",
        VodItemKind.Discover => "dis:root",
        VodItemKind.Featured => "fea:root",
        VodItemKind.Row => $"row:{Primary}:{Secondary}",
        VodItemKind.Catalog => $"cat:{Primary}",
        VodItemKind.Filter => $"flt:{Primary}:{Secondary}",
        VodItemKind.Show => $"shw:{Primary}",
        VodItemKind.ShowSeason => $"ssn:{Primary}",
        VodItemKind.LiveRoot => "liv:root",
        VodItemKind.LiveCategory => $"lcat:{Primary}",
        VodItemKind.LiveChannel => $"lch:{Primary}",
        _ => throw new InvalidOperationException($"Unknown item kind {Kind}."),
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafePart();
}
