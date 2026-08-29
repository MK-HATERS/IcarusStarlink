namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Decodes a real compiled Unreal .uasset texture from a mod's own bundled assets into PNG bytes
/// for on-screen preview. Everything else (meshes, blueprints, sounds, a corrupt/unsupported
/// asset) returns null — same "can't preview this" convention the Files tab already uses for a
/// binary asset it can't show as text or a flat image.
/// </summary>
public interface IUassetTextureDecoder
{
    /// <param name="modFolderPath">The mod's own real folder on disk (ILibraryRepository.GetFolderPath) — the whole folder is indexed, since a texture's mip data can live in a sibling .ubulk file next to its .uasset.</param>
    /// <param name="relativeAssetPath">The asset's path relative to modFolderPath, exactly as returned by ILibraryRepository.ListAssetPaths.</param>
    byte[]? TryDecodeToPng(string modFolderPath, string relativeAssetPath);
}
