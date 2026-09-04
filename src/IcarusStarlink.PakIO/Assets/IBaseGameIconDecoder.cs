namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Decodes a base-game UI icon/image reference — the raw "/Game/Path/To/Asset.AssetName" string a
/// data table's own field stores (D_Itemable's own "Icon", D_Mounts' own "Icon", D_BestiaryData's
/// own "Image" — all the same self-named-single-texture-package shape) — into PNG bytes, via
/// IBaseGameContentProvider's session-cached base-game index. A thin, single-purpose counterpart to
/// IUassetTextureDecoder: that one resolves a MOD's own bundled texture through
/// CueAssetProviderLocator (a fresh per-call index over one mod's own folder); this one resolves a
/// BASE-GAME texture through IBaseGameContentProvider (the one shared, app-lifetime-cached index
/// over Content\Paks) — a different provider entirely, so IUassetTextureDecoder's own
/// (modFolderPath, relativeAssetPath) shape doesn't fit here; this mirrors it as closely as that
/// difference allows (one path in, PNG bytes or null out, never throws).
/// </summary>
public interface IBaseGameIconDecoder
{
    /// <param name="gameIconPath">
    /// A raw "/Game/…" object reference exactly as a data table stores it — typically
    /// "Package/Path/AssetName.AssetName" (Unreal's own convention for a single-texture package
    /// named after its own one export). Only the package half (everything before the LAST '.') is
    /// actually needed to find the file; the object-name half is discarded, never assumed to match
    /// the package name even though it usually does. Null or blank returns null.
    /// </param>
    /// <returns>
    /// PNG bytes, or null if gameIconPath is null/blank, the base game content isn't available this
    /// session (see IBaseGameContentProvider's own doc comment for why), the reference doesn't match
    /// anything mounted, or the match isn't a real texture — every case degrades to null, never a
    /// thrown exception.
    /// </returns>
    byte[]? TryDecodeIconToPng(string? gameIconPath);
}
