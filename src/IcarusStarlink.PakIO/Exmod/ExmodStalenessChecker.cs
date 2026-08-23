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
    public static IReadOnlyList<StaleItem> FindLikelyStaleItems(
        ExmodPackage package, string dataFolder, ISemanticClassifier classifier,
        IDictionary<string, JsonObject?>? baseTableCache = null, MergeReport? report = null)
    {
        var changes = ExmodBaseDiffer.DiffAgainstBase(package, dataFolder, classifier, report, baseTableCache);

        return changes
            .Where(c => c.IsNewItem)
            .GroupBy(c => (c.CurrentFile, c.ItemName))
            .Select(g => new StaleItem(g.Key.CurrentFile, g.Key.ItemName, g.Count()))
            .Where(item => StaleItemHeuristic.IsLikelyStale(item.FieldCount))
            .ToList();
    }
}
