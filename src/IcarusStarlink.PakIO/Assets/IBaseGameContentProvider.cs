namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// A second, session-lifetime CUE4Parse index over Icarus's own base-game content
/// (Content\Paks\pakchunk*-WindowsNoEditor.pak — 33 files, ~44GB, confirmed unencrypted: a real
/// DefaultFileProvider mount needs zero AES key), distinct from CueAssetProviderLocator (which
/// only ever indexes ONE mod's own loose folder or extracted opaque-pak cache, never the base
/// game). Exists for exactly one reason today: a modded material's Parent can point at a
/// base-game material CueAssetProviderLocator's mod-only provider has no way to reach — see
/// CueUassetMaterialDecoder's own doc comment for the fallback this makes possible.
///
/// TryLoadExport mirrors CueAssetProviderLocator.TryLoadExport's own shape (a relative asset
/// path in, a resolved export of type T out) so a caller that already knows that pattern doesn't
/// need to learn a new one — the only real difference is there's no modFolderPath parameter,
/// since this is always the ONE fixed base-game location, mounted on first use (confirmed ~550ms
/// for all 33 paks' trailing directory indices — real per-asset decoding is separately lazy,
/// 20-150ms each), let go while the app sits idle, and mounted again on the next lookup.
/// </summary>
public interface IBaseGameContentProvider
{
    /// <param name="assetPath">
    /// A Content-relative asset path without the "/Game/" prefix, e.g.
    /// "Weapons/Materials/M_BaseWeapon.uasset" (callers strip "/Game/" themselves; only leading
    /// slashes are trimmed here) — looked up exactly, ignoring case, in an index of the mounted
    /// game's file keys with their "Icarus/Content/" prefix taken off.
    /// </param>
    /// <returns>
    /// The first export of type T in the matched package, or null if IcarusContentPath isn't set,
    /// the game's own Paks folder doesn't exist, the path doesn't match anything mounted, or the
    /// match doesn't decode as T — every case degrades to null, never a thrown exception.
    /// </returns>
    T? TryLoadExport<T>(string assetPath) where T : class;

    /// <summary>
    /// Whether the base game has a file at this /Game/-relative path ("BP/Player/BP_IcarusPlayerCharacterSurvival.uasset"):
    /// a mod's copy of it replaces the game's. Null when the game's content can't be read (no game folder set).
    /// </summary>
    bool? HasAsset(string assetPath) => null;

    /// <summary>The game's own copy of a blueprint, summarised for comparing with a mod's copy (BlueprintComparer); null when it can't be read.</summary>
    BlueprintSummary? TrySummarizeBlueprint(string assetPath) => null;

    /// <summary>
    /// The raw cooked files of the game's package at this /Game/-relative path, keyed by extension
    /// (".uasset", ".uexp"): a template for a mod's own copy (a growth curve with new keys). Null
    /// when it can't be read.
    /// </summary>
    IReadOnlyDictionary<string, byte[]>? TryReadPackageFiles(string assetPath) => null;

    /// <summary>
    /// Where the game keeps this package's files (pak, place in it, size, for its .uasset, .uexp and
    /// .ubulk): changes when a game update replaces the package, read without reading the files.
    /// Null when the game doesn't have it or its content can't be read.
    /// </summary>
    string? FileStamp(string assetPath) => null;

    /// <summary>
    /// Same lookup as TryLoadExport, but runs <paramref name="project"/> under the SAME lock that
    /// protects the load itself — required for any consumer that reads a lazily-resolved
    /// cross-package reference off the export (CueUassetMaterialDecoder's GetParams call resolving
    /// another package's Texture2D for a texture parameter is exactly this case). CUE4Parse resolves
    /// an FPackageIndex reference on first access, not at load time, against the SAME shared,
    /// session-lifetime DefaultFileProvider every other caller of this provider also uses — a
    /// caller that calls plain TryLoadExport and processes the result afterward, outside this
    /// provider's lock, would let that lazy resolution race a concurrent TryLoadExport/
    /// TryLoadExportAndProject call from another thread against the exact same shared provider
    /// instance (see CueBaseGameContentProvider's own doc comment on why that's never safe to
    /// allow). Mirrors CueAssetProviderLocator.TryLoadExportAndProject's own shape and reasoning —
    /// the mod-local case this project already established the pattern for.
    /// </summary>
    TResult? TryLoadExportAndProject<T, TResult>(string assetPath, Func<T, TResult> project)
        where T : class where TResult : class;
}
