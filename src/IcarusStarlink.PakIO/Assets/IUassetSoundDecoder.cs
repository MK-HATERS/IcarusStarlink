namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Decodes a real compiled Unreal .uasset sound (USoundWave) from a mod's own bundled assets into
/// playable audio bytes for an in-app preview. A texture, mesh, blueprint, material, or a genuinely
/// corrupt/unrecognized asset all return null — same "can't preview this" convention the other
/// decoders in this project already use. A real USoundWave that IS found but stored in a format
/// this app has no safe way to play back is NOT folded into that same null — see UassetSoundAudio's
/// own doc comment for why that distinction matters here specifically.
/// </summary>
public interface IUassetSoundDecoder
{
    /// <param name="modFolderPath">The mod's own real folder on disk (ILibraryRepository.GetFolderPath).</param>
    /// <param name="relativeAssetPath">The asset's path relative to modFolderPath, exactly as returned by ILibraryRepository.ListAssetPaths.</param>
    UassetSoundAudio? TryDecodeAudio(string modFolderPath, string relativeAssetPath);
}
