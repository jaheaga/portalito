using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PortalitoTV.Media;

/// <summary>The language an item is aired in.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum EmitLanguage
{
    /// <summary>Pick automatically: Latin American Spanish if there is one, else English + subtitle.</summary>
    Auto,

    /// <summary>Spanish audio: the track marked as Latin American, or else the first Spanish one.</summary>
    Latino,

    /// <summary>Original English audio with the Spanish subtitle burned in.</summary>
    EnglishWithSubtitles,

    /// <summary>
    /// The second Spanish track. Some titles have two unnamed "spa" tracks where the first one is Castilian Spanish; they can
    /// only be told apart by listening to them.
    /// </summary>
    SpanishSecond,
}

/// <summary>The audio track to air (index among the audio tracks) and whether a subtitle is burned in.</summary>
/// <param name="AudioOrdinal">Index among the file's audio tracks.</param>
/// <param name="BurnSubtitles">Whether the Spanish subtitle is burned in.</param>
/// <param name="Language">The language that ended up chosen.</param>
public sealed record TrackChoice(int AudioOrdinal, bool BurnSubtitles, EmitLanguage Language);

/// <summary>Picks the audio track.</summary>
public static partial class TrackSelector
{
    private static readonly string[] SpanishCodes = { "spa", "es", "esp" };
    private static readonly string[] EnglishCodes = { "eng", "en" };

    [GeneratedRegex(@"latin|lat\b|latam|es-419|mexic", RegexOptions.IgnoreCase)]
    private static partial Regex LatinTitle();

    /// <summary>Chooses the track for an item.</summary>
    /// <param name="audio">The file's audio tracks, in order.</param>
    /// <param name="wanted">The language the user asked for.</param>
    /// <param name="hasSpanishSubtitle">Whether there is a Spanish subtitle to burn in.</param>
    /// <returns>The track and whether to burn the subtitle.</returns>
    public static TrackChoice Choose(IReadOnlyList<AudioTrack> audio, EmitLanguage wanted, bool hasSpanishSubtitle)
    {
        if (audio.Count == 0)
        {
            return new TrackChoice(0, false, EmitLanguage.Latino);
        }

        var latino = FindLatino(audio);
        var english = FindFirst(audio, EnglishCodes);

        if (wanted == EmitLanguage.SpanishSecond)
        {
            var spanish = SpanishOrdinals(audio);
            if (spanish.Count >= 2)
            {
                return new TrackChoice(spanish[1], false, EmitLanguage.SpanishSecond);
            }
        }

        if (wanted == EmitLanguage.EnglishWithSubtitles && english is not null)
        {
            return new TrackChoice(english.Value, hasSpanishSubtitle, EmitLanguage.EnglishWithSubtitles);
        }

        if (latino is not null)
        {
            return new TrackChoice(latino.Value, false, EmitLanguage.Latino);
        }

        // No Spanish: the original (English if recognized, else the first) + subtitle if there is one.
        return new TrackChoice(english ?? 0, hasSpanishSubtitle, EmitLanguage.EnglishWithSubtitles);
    }

    /// <summary>Which language Auto would pick (to show it on the settings page).</summary>
    /// <param name="audio">The file's audio tracks, in order.</param>
    /// <returns>The language, or <see cref="EmitLanguage.Auto"/> if the tracks aren't known.</returns>
    public static EmitLanguage Detect(IReadOnlyList<AudioTrack> audio) =>
        audio.Count == 0 ? EmitLanguage.Auto : Choose(audio, EmitLanguage.Auto, false).Language;

    /// <summary>The indexes (among the audio tracks) of the Spanish tracks, in order.</summary>
    /// <param name="audio">The file's audio tracks, in order.</param>
    /// <returns>The indexes.</returns>
    public static List<int> SpanishOrdinals(IReadOnlyList<AudioTrack> audio) =>
        Enumerable.Range(0, audio.Count)
            .Where(i => (audio[i].Language ?? string.Empty).ToLowerInvariant() is "spa" or "es" or "esp" or "lat" or "es-419")
            .ToList();

    private static int? FindLatino(IReadOnlyList<AudioTrack> audio)
    {
        for (int i = 0; i < audio.Count; i++)
        {
            var lang = (audio[i].Language ?? string.Empty).ToLowerInvariant();
            if (lang is "lat" or "es-419" || LatinTitle().IsMatch(audio[i].Title ?? string.Empty))
            {
                return i;
            }
        }

        // The portal is Latin American: the first "spa" (the default one) is the Latin American track.
        return FindFirst(audio, SpanishCodes);
    }

    private static int? FindFirst(IReadOnlyList<AudioTrack> audio, string[] codes)
    {
        for (int i = 0; i < audio.Count; i++)
        {
            if (codes.Contains((audio[i].Language ?? string.Empty).ToLowerInvariant()))
            {
                return i;
            }
        }

        return null;
    }
}
