namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Decodes a real compiled Unreal .uasset static mesh from a mod's own bundled assets into plain
/// geometry for a 3D preview. Static meshes only; skeletal meshes have their own decoder
/// (IUassetSkeletalMeshDecoder). Everything else (a texture, blueprint, sound, skeletal mesh, or a genuinely unsupported asset)
/// returns null — same "can't preview this" convention IUassetTextureDecoder already uses.
/// </summary>
public interface IUassetStaticMeshDecoder
{
    /// <param name="modFolderPath">The mod's own real folder on disk (ILibraryRepository.GetFolderPath).</param>
    /// <param name="relativeAssetPath">The asset's path relative to modFolderPath, exactly as returned by ILibraryRepository.ListAssetPaths.</param>
    StaticMeshGeometry? TryDecodeStaticMesh(string modFolderPath, string relativeAssetPath);
}
