using CUE4Parse.UE4.Assets.Exports.Texture;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real IBaseGameIconDecoder: turns a data table's own raw "/Game/Path/Asset.Asset" icon reference
/// into the plain package-relative path IBaseGameContentProvider.TryLoadExport already expects (the
/// same "strip the /Game/ virtual-mount alias, append .uasset" idiom
/// CueUassetMaterialDecoder.TryGetUnresolvedParentAssetPath already established for a resolved
/// Parent's own package path — mirrored here, not reimplemented, just starting from a table's raw
/// Icon/Image string instead of a ResolvedObject's Outer.Name), then reuses
/// UassetTexturePngEncoder's own real "UTexture2D → PNG bytes" step — the same one
/// CueUassetTextureDecoder and CueUassetMaterialDecoder's own texture parameters already share, so
/// this is a third caller of that one real decode path, not a new one.
/// </summary>
public sealed class CueBaseGameIconDecoder : IBaseGameIconDecoder
{
    private readonly IBaseGameContentProvider _baseGameContentProvider;

    public CueBaseGameIconDecoder(IBaseGameContentProvider baseGameContentProvider) =>
        _baseGameContentProvider = baseGameContentProvider;

    public byte[]? TryDecodeIconToPng(string? gameIconPath)
    {
        var assetPath = ToAssetPath(gameIconPath);
        if (assetPath is null)
        {
            return null;
        }

        try
        {
            var texture = _baseGameContentProvider.TryLoadExport<UTexture2D>(assetPath);
            return texture is null ? null : UassetTexturePngEncoder.TryEncodeToPng(texture);
        }
        catch (Exception)
        {
            // IBaseGameContentProvider.TryLoadExport is already its own defensive boundary (never
            // throws on its own) — this is the outer safety net for anything
            // UassetTexturePngEncoder itself might still raise against a real base-game texture,
            // same "one bad reach shouldn't take down an otherwise-working preview" posture
            // CueUassetMaterialDecoder's own fallback already uses.
            return null;
        }
    }

    /// <summary>
    /// "/Game/Assets/2DArt/UI/Items/Item_Icons/Resources/ITEM_Fibre.ITEM_Fibre" →
    /// "Assets/2DArt/UI/Items/Item_Icons/Resources/ITEM_Fibre.uasset" — package paths never contain
    /// '.', so splitting on the LAST one in the raw reference is always safe and always finds the
    /// package/object boundary, regardless of whether the object name happens to mirror the
    /// package's own name (the usual case for one of these icon references, but never assumed).
    /// </summary>
    private static string? ToAssetPath(string? gameIconPath)
    {
        if (string.IsNullOrWhiteSpace(gameIconPath))
        {
            return null;
        }

        var dotIndex = gameIconPath.LastIndexOf('.');
        var packagePath = dotIndex > 0 ? gameIconPath[..dotIndex] : gameIconPath;

        var relativePath = packagePath.TrimStart('/');
        if (relativePath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = relativePath["Game/".Length..];
        }

        return relativePath.Length == 0 ? null : relativePath + ".uasset";
    }
}
