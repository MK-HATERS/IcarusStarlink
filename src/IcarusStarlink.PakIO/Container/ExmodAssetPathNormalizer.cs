namespace IcarusStarlink.PakIO.Container;

/// <summary>
/// Some mod authors' own zip tooling wraps every asset in a folder named after the mod itself —
/// confirmed against the real published JimK_Weapons_Pack_1 and JimK_Weapons_Pack_2 zips, both of
/// which consistently nest everything under "JimK_Weapons_Pack_&lt;N&gt;/", unlike every other mod
/// inspected (including this same author's own simpler mods), which place assets directly at
/// their real pak-root-relative path. Left uncorrected, every asset such a mod ships gets packed
/// one folder level deeper than its own DataTable data references (e.g. Meshable.ItemMesh:
/// "/Game/Pistols/Pistol_A"), silently breaking every mesh/icon/blueprint reference the mod's own
/// rows point at — the item's DataTable row still exists and looks complete, but nothing it
/// references can ever resolve in game.
///
/// Deliberately narrow: only strips a wrapper when EVERY asset shares the exact same single
/// top-level folder AND that folder's name exactly matches the mod's own declared FileName — a mod
/// with several distinct top-level asset folders (Outlet/, Water_Pump/, ... — the normal case,
/// confirmed against dozens of other real mods) is never touched.
/// </summary>
internal static class ExmodAssetPathNormalizer
{
    public static IReadOnlyList<ExmodAssetEntry> StripRedundantWrapperFolder(string modFileName, IReadOnlyList<ExmodAssetEntry> assets)
    {
        if (assets.Count == 0)
        {
            return assets;
        }

        string? wrapperFolder = null;
        foreach (var asset in assets)
        {
            var slashIndex = asset.RelativePath.IndexOf('/');
            if (slashIndex < 0)
            {
                // At least one asset sits directly at the root with no folder at all — there's no
                // single common wrapper to strip.
                return assets;
            }

            var firstSegment = asset.RelativePath[..slashIndex];
            if (wrapperFolder is null)
            {
                wrapperFolder = firstSegment;
            }
            else if (!string.Equals(wrapperFolder, firstSegment, StringComparison.OrdinalIgnoreCase))
            {
                return assets;
            }
        }

        if (wrapperFolder is not { } confirmedWrapperFolder || !string.Equals(confirmedWrapperFolder, modFileName, StringComparison.OrdinalIgnoreCase))
        {
            return assets;
        }

        var prefixLength = confirmedWrapperFolder.Length + 1;
        return [.. assets.Select(a => a with { RelativePath = a.RelativePath[prefixLength..] })];
    }
}
