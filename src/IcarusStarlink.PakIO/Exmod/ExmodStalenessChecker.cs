using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Proactive version of the signal TableApplier's own post-Rebuild report already surfaces per
/// queued mod — this runs the identical base-vs-modded diff (ExmodBaseDiffer) against the
/// *current* extracted game data for one mod at a time, with no Rebuild needed, so Library can
/// flag a mod as possibly stale before it's ever queued.
/// </summary>
public static class ExmodStalenessChecker
{
    public sealed record StaleItem(string CurrentFile, string ItemName, int FieldCount);

    /// <param name="baseTableCache">Pass the same dictionary across every mod in a whole-library pass — see ExmodBaseDiffer.DiffAgainstBase's own doc comment for why.</param>
    /// <param name="ownAssetPaths">
    /// The mod's own bundled binary asset paths (.uasset/.uexp/.ubulk — never .utoc/.ucas, Icarus's
    /// own real paks confirm it's the classic loose-asset format, not IoStore), if known. A "new
    /// item" whose name shows up in one of these is almost certainly real content the author added
    /// — a whole new building piece/weapon/etc. needs real compiled assets, a DataTable edit alone
    /// can't create one — so it's excluded here rather than flagged as possibly stale. Null (the
    /// default) skips this check entirely, matching every call site before this parameter existed.
    /// </param>
    public static IReadOnlyList<StaleItem> FindLikelyStaleItems(
        ExmodPackage package, string dataFolder, ISemanticClassifier classifier,
        IDictionary<string, JsonObject?>? baseTableCache = null,
        IReadOnlyList<string>? ownAssetPaths = null,
        MergeReport? report = null)
    {
        var changes = ExmodBaseDiffer.DiffAgainstBase(package, dataFolder, classifier, report, baseTableCache);

        return changes
            .Where(c => c.IsNewItem)
            .GroupBy(c => (c.CurrentFile, c.ItemName))
            .Select(g => new StaleItem(g.Key.CurrentFile, g.Key.ItemName, g.Count()))
            .Where(item => StaleItemHeuristic.IsLikelyStale(item.FieldCount))
            .Where(item => ownAssetPaths is null || !HasPlausibleOwnAsset(item.ItemName, ownAssetPaths))
            .ToList();
    }

    private static bool HasPlausibleOwnAsset(string itemName, IReadOnlyList<string> assetPaths)
    {
        foreach (var path in assetPaths)
        {
            var fileNameNoExt = Path.GetFileNameWithoutExtension(path);
            if (fileNameNoExt.Length == 0)
            {
                continue;
            }

            if (fileNameNoExt.Contains(itemName, StringComparison.OrdinalIgnoreCase)
                || itemName.Contains(fileNameNoExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
