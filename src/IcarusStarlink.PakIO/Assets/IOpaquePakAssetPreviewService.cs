namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real preview support for a .uasset packed inside an opaque/prebuilt-imported pak — unlike a
/// regular EXMOD mod's own loose files (already sitting on disk, so CueAssetProviderLocator can
/// index them directly), an opaque pak's own contents only exist packed inside its single .pak
/// file. This extracts that pak (cached — see the implementation's own doc comment on why, and
/// what it costs) into a real folder on disk, then runs it through the exact same five decoders
/// (IUassetTextureDecoder/IUassetStaticMeshDecoder/IUassetSkeletalMeshDecoder/IUassetSoundDecoder/
/// IUassetMaterialDecoder) a loose EXMOD mod's own preview already uses, in the same order
/// (LibraryItemViewModel.DecodeCompiledAsset).
/// </summary>
public interface IOpaquePakAssetPreviewService
{
    /// <param name="unrealPakExePath">Same UnrealPak.exe path Settings already stores for every other pak operation.</param>
    /// <param name="pakFilePath">The opaque mod's own single .pak file on disk.</param>
    /// <param name="relativeAssetPath">The asset's path exactly as returned by IUnrealPakService.ListPakContentsAsync (what the Files tab already lists for an opaque pak).</param>
    /// <param name="cacheDirectory">Where this pak's extracted contents are cached — one directory per distinct pak, reused across repeated preview clicks instead of re-extracting the whole pak every time.</param>
    Task<OpaquePakAssetPreviewResult> PreviewAssetAsync(
        string unrealPakExePath, string pakFilePath, string relativeAssetPath, string cacheDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Exactly one of PngBytes/Mesh/Sound/Material/FailureReason is non-null: PngBytes for a decoded
/// texture, Mesh for a decoded static OR skeletal mesh (both share this one slot — same convention
/// LibraryItemViewModel.CompiledAssetDecodeResult already uses, since both render through the same
/// Viewport3D with nothing downstream needing to tell them apart), Sound for a real USoundWave
/// (itself possibly "found but not previewable in-app" — see UassetSoundAudio's own doc comment),
/// Material for a decoded material's resolved parameter list, and FailureReason (a short,
/// already-user-facing explanation — never a raw exception) for everything else this couldn't turn
/// into a preview. Deliberately mirrors CompiledAssetDecodeResult's own shape rather than inventing
/// a parallel one, since LibraryItemViewModel branches on both results the same way.
/// </summary>
public sealed record OpaquePakAssetPreviewResult(
    byte[]? PngBytes, StaticMeshGeometry? Mesh, UassetSoundAudio? Sound, UassetMaterialParams? Material, string? FailureReason)
{
    public static OpaquePakAssetPreviewResult Decoded(
        byte[]? pngBytes = null, StaticMeshGeometry? mesh = null, UassetSoundAudio? sound = null, UassetMaterialParams? material = null) =>
        new(pngBytes, mesh, sound, material, null);

    public static OpaquePakAssetPreviewResult Failed(string reason) => new(null, null, null, null, reason);
}
