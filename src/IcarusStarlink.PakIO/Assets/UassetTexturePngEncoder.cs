using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// The actual "real Unreal texture export → PNG bytes" steps, shared by CueUassetTextureDecoder
/// (a .uasset that IS itself a texture) and CueUassetMaterialDecoder (a texture found INSIDE a
/// material's own resolved parameters) — factored out so the second caller doesn't duplicate the
/// exact TextureDecoder.Decode/TextureEncoder.ToSkBitmap/SKBitmap.Encode sequence CueUassetTextureDecoder
/// already has, rather than each maintaining its own copy of the same three calls.
/// </summary>
internal static class UassetTexturePngEncoder
{
    // A generous real-world ceiling (Direct3D's own hardware texture-dimension limit) — every real
    // UE asset this app has ever seen is far under it. .uasset files are untrusted, externally-
    // sourced input (an arbitrary downloaded/imported mod's own bundled texture, or one resolved
    // through a material), so a corrupt or maliciously crafted header declaring a negative, zero, or
    // wildly oversized SizeX/SizeY is a real, reachable input shape here — CTexture.Width/Height come
    // straight from that header, and TextureEncoder.ToSkBitmap below trusts them as the bound for
    // walking CTexture.Data's own pixel buffer. Rejecting anything outside a sane range before that
    // call closes off the most directly attacker-controllable form of an out-of-bounds read, even
    // though the decode/encode internals themselves live in the CUE4Parse/SkiaSharp dependencies
    // this app can't patch directly.
    internal const int MaxSaneDimension = 16384;

    public static byte[]? TryEncodeToPng(UTexture2D texture)
    {
        var decoded = TextureDecoder.Decode(texture, ETexturePlatform.DesktopMobile);
        if (decoded is null || !HasSaneDimensions(decoded.Width, decoded.Height, decoded.Data?.Length ?? 0))
        {
            return null;
        }

        using var bitmap = TextureEncoder.ToSkBitmap(decoded);
        using var pngData = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return pngData.ToArray();
    }

    /// <summary>
    /// Pulled out as a pure, primitive-typed predicate (rather than inlined against a real CTexture)
    /// specifically so the boundary logic itself is unit-testable without needing to construct a real
    /// CUE4Parse texture — see this class's own top doc comment for why the check exists.
    /// </summary>
    internal static bool HasSaneDimensions(int width, int height, int dataLength) =>
        width > 0 && height > 0 && width <= MaxSaneDimension && height <= MaxSaneDimension && dataLength > 0;
}
