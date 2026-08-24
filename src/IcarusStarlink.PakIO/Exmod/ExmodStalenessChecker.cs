using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Real-mod evidence this cutoff is tuned against: "Reinforced_Int_Floor" (a DataTable row
    /// name) correlates with the real bundled asset "APEX_BLD_Floor_Iron_Reinforced_Wood_INT" —
    /// sharing "reinforced"/"int"/"floor" but in a different order with an unrelated prefix/suffix
    /// — while "Prop_Specimens" sharing only the single generic word "prop" with an unrelated
    /// asset ("Prop_Surgical_Masks") in the same mod must NOT correlate. Requiring 2+ shared
    /// tokens, not 1, is what tells those two cases apart.
    /// </summary>
    private const int MinTokenOverlapForAssetCorrelation = 2;

    private static bool HasPlausibleOwnAsset(string itemName, IReadOnlyList<string> assetPaths)
    {
        var itemTokens = Tokenize(itemName);

        foreach (var path in assetPaths)
        {
            var fileNameNoExt = Path.GetFileNameWithoutExtension(path);
            if (fileNameNoExt.Length == 0)
            {
                continue;
            }

            // A real Unreal asset name occasionally does repeat a DataTable row name close to
            // verbatim (e.g. "BP_Custom_Tower" for row "Custom_Tower") — catch that directly first.
            if (fileNameNoExt.Contains(itemName, StringComparison.OrdinalIgnoreCase)
                || itemName.Contains(fileNameNoExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // More often it doesn't: real Icarus asset names carry their own prefix/category
            // convention ("APEX_BLD_Floor_Iron_Reinforced_Wood_INT" for row
            // "Reinforced_Int_Floor") and reorder the meaningful words rather than repeating the
            // row name as a literal substring. Token overlap catches that; the 2+ requirement
            // above is what keeps a single shared generic word from over-matching.
            if (itemTokens.Count >= 2 && itemTokens.Count(Tokenize(fileNameNoExt).Contains) >= MinTokenOverlapForAssetCorrelation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits on the real separators both DataTable row names and Icarus asset filenames use, plus
    /// camelCase word boundaries — real-mod evidence this is needed: row "Petes_BeaconTeleportRemote"
    /// only shares its "Petes_" prefix with its own real asset "BP_Petes_BeaconTeleport" on a plain
    /// underscore split, since neither name puts an underscore between "Beacon"/"Teleport"/"Remote" —
    /// without also splitting at each lowercase-to-uppercase transition, "beaconteleportremote" and
    /// "beaconteleport" never overlap as tokens even though they're obviously the same content.
    /// Drops anything shorter than 3 characters, which conveniently discards the common bare
    /// Unreal-convention prefixes (BP/SM/T/M) without needing an explicit stoplist for them.
    /// </summary>
    private static HashSet<string> Tokenize(string name) =>
        [.. Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", "_")
            .Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 3)];
}
