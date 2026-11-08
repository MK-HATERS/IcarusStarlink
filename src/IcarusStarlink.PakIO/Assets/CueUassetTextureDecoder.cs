using CUE4Parse.UE4.Assets.Exports.Texture;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real UE4.27 asset parsing via CUE4Parse — confirmed by direct prototyping against real mod
/// files (JimK_Weapons_Pack_1/2) that a mod's own loose bundled textures decode correctly
/// with no .usmap mappings file and no Oodle native DLL needed. Decodes a mod's own loose
/// textures; the game's own textures go through CueBaseGameIconDecoder.
/// </summary>
public sealed class CueUassetTextureDecoder : IUassetTextureDecoder
{
    public byte[]? TryDecodeToPng(string modFolderPath, string relativeAssetPath)
    {
        if (!string.Equals(Path.GetExtension(relativeAssetPath), ".uasset", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var texture = CueAssetProviderLocator.TryLoadExport<UTexture2D>(modFolderPath, relativeAssetPath);
            return texture is null ? null : UassetTexturePngEncoder.TryEncodeToPng(texture);
        }
        catch (Exception)
        {
            // A mesh, blueprint, sound, or a genuinely corrupt/unsupported asset all land here —
            // the same "no preview available" fallback the Files tab already shows for anything
            // it can't decode, rather than surfacing a raw parser exception to the user.
            return null;
        }
    }
}
