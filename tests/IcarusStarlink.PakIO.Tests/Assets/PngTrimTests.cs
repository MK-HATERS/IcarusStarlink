using IcarusStarlink.PakIO.Assets;
using SkiaSharp;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>Bestiary pictures cut to their visible part (they're mostly see-through around a small creature).</summary>
public sealed class PngTrimTests
{
    /// <summary>A see-through picture with an opaque rectangle in it.</summary>
    private static byte[] Picture(int size, SKRectI opaque)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(opaque, paint);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png)!;

    [Fact]
    public void ASmallCreatureInABigEmptyPicture_IsCutToASquareAroundIt()
    {
        var png = Picture(256, SKRectI.Create(100, 110, 40, 20));

        using var trimmed = Decode(PngTrim.TrimTransparentBorder(png, paddingPercent: 10));

        // 40 wide (the larger side) plus 10% each side = 48, square.
        Assert.Equal(48, trimmed.Width);
        Assert.Equal(48, trimmed.Height);
        Assert.Equal(SKColors.Red, trimmed.GetPixel(24, 24));
        Assert.Equal(0, trimmed.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void APictureThatFillsItself_IsLeftAsItIs()
    {
        var png = Picture(64, SKRectI.Create(0, 0, 64, 64));

        Assert.Same(png, PngTrim.TrimTransparentBorder(png));
    }

    [Fact]
    public void AnEmptyPictureOrNotAPicture_IsLeftAsItIs()
    {
        var empty = Picture(64, SKRectI.Empty);
        byte[] junk = [1, 2, 3];

        Assert.Same(empty, PngTrim.TrimTransparentBorder(empty));
        Assert.Same(junk, PngTrim.TrimTransparentBorder(junk));
    }
}
