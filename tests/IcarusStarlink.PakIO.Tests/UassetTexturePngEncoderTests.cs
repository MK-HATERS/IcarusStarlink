using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests;

/// <summary>
/// UassetTexturePngEncoder.TryEncodeToPng itself can't be unit tested directly — it needs a real
/// UTexture2D, a sealed CUE4Parse type this project has no way to construct without a real .uasset
/// file. HasSaneDimensions is the pure, primitive-typed boundary check pulled out of it specifically
/// so this part is testable: TextureDecoder.Decode's returned CTexture.Width/Height/Data come
/// straight from a .uasset's own header — untrusted, externally-sourced input (an arbitrary
/// downloaded/imported mod's own bundled texture) — and TextureEncoder.ToSkBitmap trusts them as the
/// bound for walking CTexture.Data's own pixel buffer. A corrupt or malicious header declaring a
/// negative, zero, or wildly oversized dimension is a real, reachable input shape this guards
/// against before that call.
/// </summary>
public class UassetTexturePngEncoderTests
{
    [Fact]
    public void HasSaneDimensions_OrdinaryTexture_ReturnsTrue()
    {
        Assert.True(UassetTexturePngEncoder.HasSaneDimensions(width: 512, height: 512, dataLength: 512 * 512 * 4));
    }

    [Theory]
    [InlineData(0, 512)]
    [InlineData(-1, 512)]
    [InlineData(512, 0)]
    [InlineData(512, -1)]
    public void HasSaneDimensions_NonPositiveWidthOrHeight_ReturnsFalse(int width, int height)
    {
        Assert.False(UassetTexturePngEncoder.HasSaneDimensions(width, height, dataLength: 1024));
    }

    [Theory]
    [InlineData(int.MaxValue, 512)]
    [InlineData(512, int.MaxValue)]
    [InlineData(UassetTexturePngEncoder.MaxSaneDimension + 1, 512)]
    public void HasSaneDimensions_DimensionBeyondSaneCeiling_ReturnsFalse(int width, int height)
    {
        Assert.False(UassetTexturePngEncoder.HasSaneDimensions(width, height, dataLength: 1024));
    }

    [Fact]
    public void HasSaneDimensions_AtTheSaneCeiling_ReturnsTrue()
    {
        Assert.True(UassetTexturePngEncoder.HasSaneDimensions(
            UassetTexturePngEncoder.MaxSaneDimension, UassetTexturePngEncoder.MaxSaneDimension, dataLength: 1024));
    }

    [Fact]
    public void HasSaneDimensions_EmptyDataBuffer_ReturnsFalse()
    {
        Assert.False(UassetTexturePngEncoder.HasSaneDimensions(width: 512, height: 512, dataLength: 0));
    }
}
