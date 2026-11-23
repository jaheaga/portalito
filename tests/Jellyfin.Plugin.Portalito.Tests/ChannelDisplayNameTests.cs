using Jellyfin.Plugin.Portalito.Channels;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// Cleaning raw Xtream live-channel names for display: strip country prefixes, confusable glyphs, quality and
/// operator tags and decorations; fix in-word leetspeak; keep the brand, the channel number, source casing, and a
/// tidy "· CC QUALITY #op" suffix. Names below are synthetic examples of the decoration patterns (ScrubGuard-clean).
/// </summary>
public class ChannelDisplayNameTests
{
    [Theory]
    // country prefix + confusable + quality
    [InlineData("AR | E$PN 2 HD", "ESPN 2 · AR HD")]
    [InlineData("PE| E$PN 2 SD", "ESPN 2 · PE SD")]
    [InlineData("E$PN 3 SD", "ESPN 3 · SD")]
    // '+' kept, quality label normalized, highest wins
    [InlineData("CO | Win Sports+ 4K", "Win Sports+ · CO 4K")]
    [InlineData("CO | WIN SPORTS +  FHD", "WIN SPORTS+ · CO FHD")]
    // single-letter obfuscation collapses, symbol -> letter
    [InlineData("AR | T*Y*C SPORTS HD", "TYC SPORTS · AR HD")]
    [InlineData("AR | F⚽X SPORTS 1 HD", "FOX SPORTS 1 · AR HD")]
    // decorations dropped, '!' between letters -> I
    [InlineData("CR | ¥!GO SPORTS HD ⭐", "YIGO SPORTS · CR HD")]
    // in-word leet fixed, standalone number kept
    [InlineData("PE| G⚽L P3RU  HD", "GOL PERU · PE HD")]
    [InlineData("PE | L1GA 1 M4X HD", "LIGA 1 MAX · PE HD")]
    // operator index kept as a distinguishing suffix
    [InlineData("CL | *T*N*T SPORTS HD / OP / 04", "TNT SPORTS · CL HD #4")]
    [InlineData("MX | TUDN OP 2", "TUDN · MX #2")]
    // a lowercase l/i between two capitals is a disguised 'I'
    [InlineData("DAZN LlGA OP 1", "DAZN LIGA · #1")]
    // lone-letter pipe, 3-letter country folds to 2
    [InlineData("ECU: EL CANAL DEL FUTBOL", "EL CANAL DEL FUTBOL · EC")]
    // already-clean names: parenthesized resolution becomes the suffix, rest preserved
    [InlineData("beIN Sports XTRA en Espanol (1080p)", "beIN Sports XTRA en Espanol · 1080p")]
    [InlineData("BAND SPORTS HD", "BAND SPORTS · HD")]
    [InlineData("COMBATE SD", "COMBATE · SD")]
    public void Cleans_decorated_names(string raw, string expected)
        => Assert.Equal(expected, ChannelDisplayName.Clean(raw));

    [Theory]
    // nothing meaningful survives: keep the original rather than show a blank
    [InlineData("#2")]
    [InlineData("# 3")]
    public void Keeps_the_original_when_nothing_survives(string raw)
        => Assert.Equal(raw, ChannelDisplayName.Clean(raw));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Handles_empty_input(string? raw, string expected)
        => Assert.Equal(expected, ChannelDisplayName.Clean(raw));

    [Fact]
    public void Leaves_a_plain_name_unchanged()
        => Assert.Equal("Discovery Channel", ChannelDisplayName.Clean("Discovery Channel"));

    [Fact]
    public void Does_not_treat_a_leading_non_country_word_as_a_prefix()
        => Assert.Equal("DO IT TV", ChannelDisplayName.Clean("DO IT TV"));

    [Fact]
    public void Leaves_a_mixed_case_name_with_an_inner_i_alone()
        => Assert.Equal("Win Sports", ChannelDisplayName.Clean("Win Sports"));
}
