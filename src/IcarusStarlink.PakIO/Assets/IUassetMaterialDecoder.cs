namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Decodes a real compiled Unreal .uasset material (a plain UMaterial, or a UMaterialInstance/
/// UMaterialInstanceConstant override) into its own resolved parameter list — never a rendered
/// preview, just which textures/colors/scalars it actually sets and its BlendMode/ShadingModel.
/// Everything else (a texture, a mesh, a sound, a blueprint, or a genuinely unsupported/corrupt
/// asset) returns null — same "can't preview this" convention every other decoder here uses.
/// </summary>
public interface IUassetMaterialDecoder
{
    /// <param name="modFolderPath">The mod's own real folder on disk (ILibraryRepository.GetFolderPath).</param>
    /// <param name="relativeAssetPath">The asset's path relative to modFolderPath, exactly as returned by ILibraryRepository.ListAssetPaths.</param>
    UassetMaterialParams? TryDecodeMaterial(string modFolderPath, string relativeAssetPath);
}
