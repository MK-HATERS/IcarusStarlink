using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

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
            var versions = new VersionContainer(EGame.GAME_UE4_27);
            var provider = new DefaultFileProvider(modFolderPath, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase);
            provider.Initialize();

            var normalizedRelativePath = relativeAssetPath.Replace('\\', '/').TrimStart('/');
            var matchedKey = provider.Files.Keys.FirstOrDefault(key => key.EndsWith(normalizedRelativePath, StringComparison.OrdinalIgnoreCase));
            if (matchedKey is null)
            {
                return null;
            }

            var package = provider.LoadPackage(matchedKey);
            var texture = package.ExportsLazy.Select(export => export.Value).OfType<UTexture2D>().FirstOrDefault();
            if (texture is null)
            {
                return null;
            }

            var decoded = TextureDecoder.Decode(texture, ETexturePlatform.DesktopMobile);
            if (decoded is null)
            {
                return null;
            }

            using var bitmap = TextureEncoder.ToSkBitmap(decoded);
            using var pngData = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return pngData.ToArray();
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
