using SkiaSharp;

namespace Jellyfin.Plugin.Portalito.Collage;

/// <summary>How each image fills its cell.</summary>
public enum CollageFit
{
    /// <summary>Posters: scaled to cover the cell, center-cropped.</summary>
    Cover,

    /// <summary>Channel logos: scaled to fit whole inside the cell, on the dark background.</summary>
    Contain,
}

/// <summary>
/// Draws a folder thumbnail: a 2x2 grid of the folder's own images with its name on a band across the bottom (owner
/// choice 2026-09-24). Portrait, poster-shaped, so it sits in the same grid as the titles. Jellyfin's own
/// <c>CreateImageCollage</c> only does a plain grid or a wide strip, so this draws with the SkiaSharp Jellyfin ships.
/// </summary>
public static class CollageRenderer
{
    public const int Width = 600;
    public const int Height = 900;

    private const int BandHeight = 150;
    private static readonly SKColor Background = new(0x14, 0x18, 0x1c);

    /// <summary>
    /// JPEG bytes. Up to four images, repeated to fill the grid when there are fewer; ones that don't decode are
    /// skipped. With none at all the tile is just the name on the dark background, so every folder gets a thumbnail.
    /// </summary>
    public static byte[] Render(IReadOnlyList<byte[]> images, string label, CollageFit fit)
    {
        var bitmaps = images.Select(TryDecode).OfType<SKBitmap>().Take(4).ToList();
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
            var canvas = surface.Canvas;
            canvas.Clear(Background);

            if (bitmaps.Count > 0)
            {
                // Posters run under the band (it only darkens them); logos stay above it, whole.
                const int cellWidth = Width / 2;
                var cellHeight = (fit == CollageFit.Contain ? Height - BandHeight : Height) / 2;
                for (var cell = 0; cell < 4; cell++)
                {
                    var target = SKRect.Create(cell % 2 * cellWidth, cell / 2 * cellHeight, cellWidth, cellHeight);
                    DrawInto(canvas, bitmaps[cell % bitmaps.Count], target, fit);
                }
            }

            DrawBand(canvas, label);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            return data.ToArray();
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    private static SKBitmap? TryDecode(byte[] bytes)
    {
        try
        {
            return SKBitmap.Decode(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void DrawInto(SKCanvas canvas, SKBitmap bitmap, SKRect cell, CollageFit fit)
    {
        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        if (fit == CollageFit.Cover)
        {
            // Crop the source to the cell's aspect ratio, centered, then scale it to the cell.
            var scale = Math.Max(cell.Width / bitmap.Width, cell.Height / bitmap.Height);
            var sourceWidth = cell.Width / scale;
            var sourceHeight = cell.Height / scale;
            var source = SKRect.Create((bitmap.Width - sourceWidth) / 2, (bitmap.Height - sourceHeight) / 2, sourceWidth, sourceHeight);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, source, cell, sampling, paint);
        }
        else
        {
            var inner = SKRect.Inflate(cell, -cell.Width * 0.12f, -cell.Height * 0.12f);
            var scale = Math.Min(inner.Width / bitmap.Width, inner.Height / bitmap.Height);
            var width = bitmap.Width * scale;
            var height = bitmap.Height * scale;
            var target = SKRect.Create(inner.MidX - (width / 2), inner.MidY - (height / 2), width, height);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, target, sampling, paint);
        }
    }

    private static void DrawBand(SKCanvas canvas, string label)
    {
        using (var band = new SKPaint { Color = new SKColor(0, 0, 0, 190) })
        {
            canvas.DrawRect(SKRect.Create(0, Height - BandHeight, Width, BandHeight), band);
        }

        // DejaVu Sans is what the Jellyfin container has (and covers Spanish accents); the default face otherwise.
        using var typeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 72);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        const float maxWidth = Width - 48;
        var width = font.MeasureText(label);
        if (width > maxWidth)
        {
            font.Size *= maxWidth / width;
            width = font.MeasureText(label);
        }

        var baseline = Height - (BandHeight / 2f) + (font.Metrics.CapHeight / 2f);
        canvas.DrawText(label, (Width - width) / 2f, baseline, SKTextAlign.Left, font, text);
    }
}
