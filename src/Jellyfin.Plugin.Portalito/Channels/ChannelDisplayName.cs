using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Portalito.Channels;

/// <summary>
/// Turns a raw Xtream live-channel name into a clean, readable display name. Panel names are decorated with
/// country/provider prefixes (<c>AR |</c>), confusable glyphs (<c>E$PN</c>, <c>F⚽X</c>), leetspeak digits
/// (<c>$P0RTS</c>, <c>L1GA</c>), quality tags (<c>HD</c>/<c>SD</c>/<c>4K</c>), operator/backup tags
/// (<c>OP 02</c>) and emoji (<c>⭐</c>). This strips the noise but keeps the brand, the channel number and the
/// source casing, moving country, quality and operator into a tidy <c>· AR HD #2</c> suffix so channels that
/// differ only by those stay distinct.
/// <para>
/// Unlike <see cref="Metadata.CommunityLogoResolver"/>'s matching normalizer -- which lowercases and drops the
/// trailing channel number to collapse variants for lookup -- this preserves numbers and casing for display,
/// so it keeps its own constant sets rather than sharing them.
/// </para>
/// </summary>
public static class ChannelDisplayName
{
    // Country/provider codes that appear as a leading prefix; 3-letter forms fold to their 2-letter code.
    private static readonly IReadOnlyDictionary<string, string> CountryCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "AR", ["bo"] = "BO", ["br"] = "BR", ["ca"] = "CA", ["cl"] = "CL", ["co"] = "CO", ["cr"] = "CR",
        ["do"] = "DO", ["ec"] = "EC", ["es"] = "ES", ["gt"] = "GT", ["hn"] = "HN", ["mx"] = "MX", ["ni"] = "NI",
        ["pa"] = "PA", ["pe"] = "PE", ["pr"] = "PR", ["py"] = "PY", ["sv"] = "SV", ["us"] = "US", ["uy"] = "UY",
        ["ve"] = "VE",
        ["arg"] = "AR", ["bol"] = "BO", ["bra"] = "BR", ["chi"] = "CL", ["col"] = "CO", ["ecu"] = "EC",
        ["esp"] = "ES", ["mex"] = "MX", ["par"] = "PY", ["per"] = "PE", ["uru"] = "UY", ["usa"] = "US",
        ["ven"] = "VE",
    };

    // Symbol / Greek / small-capital look-alikes mapped to their plain UPPERCASE Latin letter (the case is then
    // chosen from the surrounding letters at mapping time). FormKD folding later handles circled letters,
    // superscripts and full-width forms. '!' is handled separately (letter 'I' only between letters).
    private static readonly IReadOnlyDictionary<char, char> SymbolToLetter = new Dictionary<char, char>
    {
        // symbols
        ['$'] = 'S', ['€'] = 'E', ['¢'] = 'C', ['₱'] = 'P', ['¥'] = 'Y', ['∆'] = 'A', ['Δ'] = 'A', ['⚽'] = 'O',
        // Greek capitals that look Latin
        ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H', ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M',
        ['Ν'] = 'N', ['Ο'] = 'O', ['Ρ'] = 'P', ['Τ'] = 'T', ['Υ'] = 'Y', ['Χ'] = 'X',
        // Latin small capitals / phonetic look-alikes
        ['ᴀ'] = 'A', ['ʙ'] = 'B', ['ᴄ'] = 'C', ['ᴅ'] = 'D', ['ᴇ'] = 'E', ['ꜰ'] = 'F', ['ɢ'] = 'G', ['ʜ'] = 'H',
        ['ɪ'] = 'I', ['ᴊ'] = 'J', ['ᴋ'] = 'K', ['ʟ'] = 'L', ['ᴍ'] = 'M', ['ɴ'] = 'N', ['ᴏ'] = 'O', ['ᴘ'] = 'P',
        ['ꞯ'] = 'Q', ['ʀ'] = 'R', ['ꜱ'] = 'S', ['ᴛ'] = 'T', ['ᴜ'] = 'U', ['ᴠ'] = 'V', ['ʏ'] = 'Y',
    };

    // Leetspeak digits mapped to letters, but only when the digit sits inside a word (a letter on either side);
    // a token that is all digits is a channel number and is left alone.
    private static readonly IReadOnlyDictionary<char, char> LeetToLetter = new Dictionary<char, char>
    {
        ['0'] = 'O', ['1'] = 'I', ['3'] = 'E', ['4'] = 'A',
    };

    // Quality tag (any form) -> canonical label, with a rank so the best one wins when several appear.
    private static readonly IReadOnlyDictionary<string, string> QualityLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sd"] = "SD", ["480p"] = "480p", ["hd"] = "HD", ["720p"] = "720p", ["fhd"] = "FHD", ["fullhd"] = "FHD",
        ["1080p"] = "1080p", ["uhd"] = "UHD", ["4k"] = "4K",
    };

    private static readonly IReadOnlyDictionary<string, int> QualityRank = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["SD"] = 1, ["480p"] = 1, ["HD"] = 2, ["720p"] = 2, ["FHD"] = 3, ["1080p"] = 3, ["UHD"] = 4, ["4K"] = 4,
    };

    private static readonly HashSet<string> OpKeywords = new(StringComparer.OrdinalIgnoreCase) { "op", "opc", "alt", "backup" };

    // A leading "<CC> |" / "<CC>:" / "<CC>/" / "<CC> l " prefix (the lone l/i is the pipe some panels type as a letter).
    private static readonly Regex PrefixRegex = new(
        @"^(?<cc>[A-Za-z]{2,3})\s*(?:[|:/]+|(?<=.\s)[liLI](?=\s))\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ParenQuality = new(
        @"\(\s*(?<q>\d{3,4}p|4k|uhd|fhd|fullhd|hd|sd)\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A channel number glued to a quality suffix, e.g. "1HD" -> number 1, quality HD.
    private static readonly Regex GluedQuality = new(
        @"^(\d+)(hd|sd|fhd|uhd|4k)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The cleaned display name; the original (trimmed) when cleaning would leave almost nothing.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw?.Trim() ?? string.Empty;
        }

        var original = raw.Trim();

        // 1. A leading country/provider prefix (only when the token really is a known code).
        string? country = null;
        var rest = original;
        var prefix = PrefixRegex.Match(rest);
        if (prefix.Success && CountryCodes.TryGetValue(prefix.Groups["cc"].Value, out var code))
        {
            country = code;
            rest = rest[prefix.Length..];
        }

        // 2. Parenthesized resolution/quality, e.g. "(1080p)".
        string? quality = null;
        rest = ParenQuality.Replace(rest, m => { ConsiderQuality(m.Groups["q"].Value, ref quality); return " "; });

        // 3. Fold with FormKD first (so superscript/circled/full-width forms become their base letter), then map
        //    confusables -- the fold can surface a small-capital look-alike (e.g. a superscript "ᶦ" -> "ɪ") that the
        //    map then turns into a plain letter. Keep only letters/digits/'+'/brackets (everything else -> space).
        var folded = MapConfusables(rest.Normalize(NormalizationForm.FormKD));
        var kept = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            kept.Append(char.IsLetterOrDigit(ch) || ch is '+' or '[' or ']' ? ch : ' ');
        }

        // 4. Tokenize and rejoin runs of single letters ("T N T" -> "TNT", from "*T*N*T*").
        var tokens = CollapseSingleLetters(kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // 5. Pull quality / operator out of the tokens; everything else is body (with in-word leet fixed).
        string? op = null;
        var body = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (t.All(c => c == '+'))
            {
                if (body.Count > 0)
                {
                    body[^1] += "+";
                }

                continue;
            }

            if (QualityLabel.ContainsKey(t))
            {
                ConsiderQuality(t, ref quality);
                continue;
            }

            var glued = GluedQuality.Match(t);
            if (glued.Success)
            {
                ConsiderQuality(glued.Groups[2].Value, ref quality);
                t = glued.Groups[1].Value;
            }

            if (OpKeywords.Contains(t))
            {
                if (i + 1 < tokens.Count && tokens[i + 1].All(char.IsDigit))
                {
                    op = TrimLeadingZeros(tokens[i + 1]);
                    i++;
                }

                continue;
            }

            body.Add(FixLeet(t));
        }

        var bodyText = string.Join(' ', body).Trim();
        if (bodyText.Length < 2)
        {
            // Nothing meaningful survived (e.g. "#2", "# 3"); keep the original rather than show a blank.
            return original;
        }

        var suffix = new List<string>(3);
        if (country is not null)
        {
            suffix.Add(country);
        }

        if (quality is not null)
        {
            suffix.Add(quality);
        }

        if (op is not null)
        {
            suffix.Add("#" + op);
        }

        return suffix.Count == 0 ? bodyText : bodyText + " · " + string.Join(' ', suffix);
    }

    private static void ConsiderQuality(string token, ref string? best)
    {
        if (!QualityLabel.TryGetValue(token, out var label))
        {
            return;
        }

        if (best is null || QualityRank[label] > QualityRank[best])
        {
            best = label;
        }
    }

    /// <summary>Maps symbol/Greek/small-cap look-alikes to letters, with the case taken from the surrounding letters.</summary>
    private static string MapConfusables(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (SymbolToLetter.TryGetValue(ch, out var upper))
            {
                sb.Append(CaseFrom(sb, s, i) ? char.ToLowerInvariant(upper) : upper);
            }
            else if (ch == '!' && PrevIsLetter(sb) && NextIsLetter(s, i))
            {
                // '!' between letters is a disguised 'I' (V!GO, D!SNEY); a stray '!' is dropped below.
                sb.Append(CaseFrom(sb, s, i) ? 'i' : 'I');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    // A mapped letter is lowercase when the nearest letter (the previous output, else the next input) is lowercase.
    private static bool CaseFrom(StringBuilder sb, string s, int i)
    {
        for (var j = sb.Length - 1; j >= 0; j--)
        {
            if (char.IsLetter(sb[j]))
            {
                return char.IsLower(sb[j]);
            }
        }

        for (var j = i + 1; j < s.Length; j++)
        {
            if (char.IsLetter(s[j]))
            {
                return char.IsLower(s[j]);
            }
        }

        return false;
    }

    private static bool PrevIsLetter(StringBuilder sb) => sb.Length > 0 && char.IsLetter(sb[^1]);

    private static bool NextIsLetter(string s, int i) => i + 1 < s.Length && char.IsLetter(s[i + 1]);

    private static List<string> CollapseSingleLetters(string[] tokens)
    {
        var result = new List<string>(tokens.Length);
        var run = new List<string>();

        void Flush()
        {
            if (run.Count >= 2)
            {
                result.Add(string.Concat(run));
            }
            else
            {
                result.AddRange(run);
            }

            run.Clear();
        }

        foreach (var t in tokens)
        {
            if (t.Length == 1 && char.IsLetter(t[0]))
            {
                run.Add(t);
            }
            else
            {
                Flush();
                result.Add(t);
            }
        }

        Flush();
        return result;
    }

    private static string FixLeet(string token)
    {
        if (token.All(char.IsDigit))
        {
            return token; // a channel number, left as typed
        }

        var sb = new StringBuilder(token.Length);
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (LeetToLetter.TryGetValue(ch, out var letter)
                && (i > 0 && char.IsLetter(token[i - 1]) || i + 1 < token.Length && char.IsLetter(token[i + 1])))
            {
                var neighbour = i > 0 && char.IsLetter(token[i - 1]) ? token[i - 1] : token[i + 1];
                sb.Append(char.IsLower(neighbour) ? char.ToLowerInvariant(letter) : letter);
            }
            else if ((ch == 'l' || ch == 'i')
                && i > 0 && char.IsUpper(token[i - 1])
                && i + 1 < token.Length && char.IsUpper(token[i + 1]))
            {
                // A lowercase l/i wedged between two capitals is a disguised 'I' ("DAZN LlGA" -> "DAZN LIGA").
                // Both neighbours must be capitals, so clean mixed-case names like "Win" are left untouched.
                sb.Append('I');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string TrimLeadingZeros(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}
