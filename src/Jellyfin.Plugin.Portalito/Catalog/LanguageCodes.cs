namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>
/// Maps the portal's subtitle language values to the ISO 639-2 codes Jellyfin matches users' language preferences
/// against (the same codes ffprobe reads off the audio tracks: <c>spa</c>, <c>eng</c>, <c>por</c>). The portal's
/// exact format isn't pinned down -- two-letter codes ("es", the reference client's captured <c>es.srt</c>/<c>en.srt</c>), and
/// the reference client's preferences also carry names like "LATINO"/"ENGLISH" -- so both forms are accepted.
/// </summary>
public static class LanguageCodes
{
    private static readonly Dictionary<string, string> Iso6392 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = "spa", ["spa"] = "spa", ["esp"] = "spa", ["spanish"] = "spa", ["español"] = "spa", ["espanol"] = "spa",
        ["latino"] = "spa", ["castellano"] = "spa", ["es-419"] = "spa", ["es-es"] = "spa", ["es-mx"] = "spa",
        ["en"] = "eng", ["eng"] = "eng", ["english"] = "eng", ["inglés"] = "eng", ["ingles"] = "eng",
        ["pt"] = "por", ["por"] = "por", ["portuguese"] = "por", ["português"] = "por", ["portugues"] = "por", ["pt-br"] = "por",
        ["fr"] = "fre", ["fre"] = "fre", ["fra"] = "fre", ["french"] = "fre", ["francés"] = "fre",
        ["de"] = "ger", ["ger"] = "ger", ["deu"] = "ger", ["german"] = "ger", ["alemán"] = "ger",
        ["it"] = "ita", ["ita"] = "ita", ["italian"] = "ita", ["italiano"] = "ita",
        ["ja"] = "jpn", ["jpn"] = "jpn", ["japanese"] = "jpn", ["japonés"] = "jpn",
        ["ko"] = "kor", ["kor"] = "kor", ["korean"] = "kor", ["coreano"] = "kor",
        ["zh"] = "chi", ["chi"] = "chi", ["zho"] = "chi", ["chinese"] = "chi", ["chino"] = "chi",
    };

    /// <summary>Returns the ISO 639-2 code for <paramref name="portalValue"/>, or null when it isn't a known language.</summary>
    public static string? ToIso6392(string? portalValue)
        => portalValue is { Length: > 0 } v && Iso6392.TryGetValue(v.Trim(), out var code) ? code : null;
}
