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
    public static byte[]? TryEncodeToPng(UTexture2D texture)
    {
        var decoded = TextureDecoder.Decode(texture, ETexturePlatform.DesktopMobile);
        if (decoded is null)
        {
            return null;
        }

        using var bitmap = TextureEncoder.ToSkBitmap(decoded);
        using var pngData = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return pngData.ToArray();
    }
}
