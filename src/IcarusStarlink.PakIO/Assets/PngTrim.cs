using SkiaSharp;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Cuts the see-through border off a picture. The game's bestiary pictures (D_BestiaryData Image)
/// are mostly empty: the creature sits small in the middle (the game zooms them itself, with
/// PopupImageScale), so shown whole in a small tile the creature was a few pixels big.
/// </summary>
public static class PngTrim
{
    /// <summary>A pixel this opaque or more counts as part of the picture (soft shadows and fringes don't).</summary>
    private const byte VisibleAlpha = 24;

    /// <summary>
    /// The picture cut to its visible part, plus <paramref name="paddingPercent"/> of that size on
    /// each side, as a square PNG (so it fills a square tile); the original bytes when nothing would
    /// be cut, it's fully see-through, or it isn't a picture.
    /// </summary>
    public static byte[] TrimTransparentBorder(byte[] png, int paddingPercent = 6)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(png);
            if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0)
            {
                return png;
            }

            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            var pixels = bitmap.Pixels;
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (pixels[y * bitmap.Width + x].Alpha >= VisibleAlpha)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            if (right < 0)
            {
                return png;
            }

            // A square around the visible part, padded, kept inside the picture where it can be.
            var side = Math.Max(right - left + 1, bottom - top + 1);
            side += 2 * side * paddingPercent / 100;
            if (side >= Math.Max(bitmap.Width, bitmap.Height))
            {
                return png;
            }

            var centreX = (left + right + 1) / 2;
            var centreY = (top + bottom + 1) / 2;
            var source = SKRectI.Create(centreX - side / 2, centreY - side / 2, side, side);

            using var square = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(square))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(bitmap, source, SKRect.Create(0, 0, side, side));
            }

            using var data = square.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception)
        {
            return png;
        }
    }
}
