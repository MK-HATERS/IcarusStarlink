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
/// since this is always the ONE fixed base-game location, mounted at most once per app session
/// (confirmed ~550ms for all 33 paks' trailing directory indices — real per-asset decoding is
/// separately lazy, 20-150ms each) and reused for every call after that.
/// </summary>
public interface IBaseGameContentProvider
{
    /// <param name="assetPath">
    /// A /Game/-relative (or plain relative — a leading "/Game/" is stripped the same way) asset
    /// path, e.g. "Weapons/Materials/M_BaseWeapon.uasset" — matched against the mounted base
    /// game's own file keys the same PathBoundaryMatch suffix rule CueAssetProviderLocator itself
    /// uses, since CUE4Parse's real internal keys carry their own pakchunk-relative prefix ahead
    /// of this plain path.
    /// </param>
    /// <returns>
    /// The first export of type T in the matched package, or null if IcarusContentPath isn't set,
    /// the game's own Paks folder doesn't exist, the path doesn't match anything mounted, or the
    /// match doesn't decode as T — every case degrades to null, never a thrown exception.
    /// </returns>
    T? TryLoadExport<T>(string assetPath) where T : class;
}
