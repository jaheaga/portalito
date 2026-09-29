using Jellyfin.Plugin.Portalito.Collage;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class CollageRendererTests
{
    private static byte[] Solid(SKColor color, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] jpeg) => SKBitmap.Decode(jpeg);

    [Fact]
    public void Four_posters_fill_the_four_cells_and_the_name_band_sits_at_the_bottom()
    {
        var posters = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow }.Select(c => Solid(c, 200, 300)).ToList();

        using var collage = Decode(CollageRenderer.Render(posters, "Acción", CollageFit.Cover));

        Assert.Equal(CollageRenderer.Width, collage.Width);
        Assert.Equal(CollageRenderer.Height, collage.Height);
        Assert.True(collage.GetPixel(150, 200).Red > 200);
        Assert.True(collage.GetPixel(450, 200).Green > 200);
        Assert.True(collage.GetPixel(150, 600).Blue > 200);
        // Under the band the yellow poster is darkened, not hidden.
        var banded = collage.GetPixel(590, 890);
        Assert.InRange(banded.Red, 20, 120);
    }

    [Fact]
    public void Fewer_posters_repeat_and_undecodable_ones_are_skipped()
    {
        var posters = new List<byte[]> { new byte[] { 1, 2, 3 }, Solid(SKColors.Red, 100, 100) };

        using var collage = Decode(CollageRenderer.Render(posters, "Terror", CollageFit.Cover));

        Assert.All(new[] { (150, 200), (450, 200), (150, 600), (450, 600) }, p => Assert.True(collage.GetPixel(p.Item1, p.Item2).Red > 200));
    }

    [Fact]
    public void No_posters_still_gives_a_name_tile()
    {
        using var collage = Decode(CollageRenderer.Render(Array.Empty<byte[]>(), "Un nombre de carpeta muy, muy largo para la banda", CollageFit.Contain));

        Assert.Equal(CollageRenderer.Width, collage.Width);
        Assert.Equal(CollageRenderer.Height, collage.Height);
    }
}
