using Jellyfin.Plugin.Portalito.Channels;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>Reading the show/movie title out of a "24/7 ..." VOD-loop channel name (for the TMDB poster fallback).</summary>
public class LoopTitleTests
{
    [Theory]
    [InlineData("24/7 Betty la fea", "Betty la fea")]
    [InlineData("24/7  Chespirito", "Chespirito")]
    [InlineData("24/7 ll EL CHAVO DEL OCHO", "EL CHAVO DEL OCHO")]
    [InlineData("24/7 | DRAGON BALL Z", "DRAGON BALL Z")]
    [InlineData("24/7 | Accion HD", "Accion")]   // trailing quality tag dropped
    [InlineData("247 Futurama", "Futurama")]     // no slash
    [InlineData("24-7 Blue", "Blue")]            // dash variant
    public void Reads_the_title_after_the_24_7_marker(string name, string expected)
        => Assert.Equal(expected, PortalitoVodChannel.LoopTitle(name));

    [Theory]
    [InlineData("AR | A24  HD")]          // a real broadcast channel, not a loop
    [InlineData("Caracol TV")]
    [InlineData("24/7")]                   // marker only, no title
    [InlineData("2470 Something")]         // "247" must be the 24/7 marker, not part of a number
    public void Returns_null_when_it_is_not_a_loop_channel(string name)
        => Assert.Null(PortalitoVodChannel.LoopTitle(name));
}
