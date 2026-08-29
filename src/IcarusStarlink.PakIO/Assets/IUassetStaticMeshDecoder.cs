namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Decodes a real compiled Unreal .uasset static mesh from a mod's own bundled assets into plain
/// geometry for a 3D preview. Skeletal meshes (bone weights, skin transforms) are deliberately out
/// of scope — most real weapon meshes are skeletal, so this covers static props/parts only for now.
/// Everything else (a texture, blueprint, sound, skeletal mesh, or a genuinely unsupported asset)
/// returns null — same "can't preview this" convention IUassetTextureDecoder already uses.
/// </summary>
public interface IUassetStaticMeshDecoder
{
    /// <param name="modFolderPath">The mod's own real folder on disk (ILibraryRepository.GetFolderPath).</param>
    /// <param name="relativeAssetPath">The asset's path relative to modFolderPath, exactly as returned by ILibraryRepository.ListAssetPaths.</param>
    StaticMeshGeometry? TryDecodeStaticMesh(string modFolderPath, string relativeAssetPath);
}
