namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Decodes a real compiled Unreal .uasset skeletal mesh from a mod's own bundled assets into the
/// exact same plain geometry shape IUassetStaticMeshDecoder already produces — LibraryItemViewModel's
/// own BuildMeshModel/Viewport3D preview needs no change at all to show either one. Deliberately
/// bind-pose (rest pose) only: bone weights and skinning transforms are never applied, so a mesh
/// with an unusual resting pose (a spread-eagled T-pose, etc.) previews exactly as its own rest
/// pose looks, not as it would appear in-game — animation/skinning math is out of scope, this app
/// has none today. Everything else (a texture, static mesh, sound, blueprint, or a genuinely
/// unsupported/corrupt asset) returns null — same "can't preview this" convention every other
/// decoder here already uses.
/// </summary>
public interface IUassetSkeletalMeshDecoder
{
    /// <param name="modFolderPath">The mod's own real folder on disk (ILibraryRepository.GetFolderPath).</param>
    /// <param name="relativeAssetPath">The asset's path relative to modFolderPath, exactly as returned by ILibraryRepository.ListAssetPaths.</param>
    StaticMeshGeometry? TryDecodeSkeletalMesh(string modFolderPath, string relativeAssetPath);
}
