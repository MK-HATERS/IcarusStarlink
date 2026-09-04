using CUE4Parse.UE4.Assets.Exports.Texture;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real UE4.27 asset parsing via CUE4Parse — confirmed by direct prototyping against the user's
/// own mod files (JimK_Weapons_Pack_1/2) that a mod's own loose bundled textures decode correctly
/// with no .usmap mappings file and no Oodle native DLL needed. Icarus's base-game pak-chunk
/// assets weren't verified the same way and may behave differently (see IUassetTextureDecoder's
/// own scope note) — this is deliberately mod-assets-only for now.
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
