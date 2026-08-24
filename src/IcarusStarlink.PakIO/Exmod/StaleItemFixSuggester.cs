using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <param name="SuggestedItemName">The closest real row name found in the same base file.</param>
/// <param name="CanAutoApply">
/// True only when the match is unambiguous (no other candidate close behind) AND the candidate's
/// own real fields plausibly cover what the mod is trying to set. Anything less certain still gets
/// a suggestion, just not one this app will act on without a human confirming it.
/// </param>
public sealed record StaleItemFixSuggestion(string SuggestedItemName, bool CanAutoApply);

/// <summary>
/// Best-effort "did you mean X?" for a flagged stale item (ExmodStalenessChecker) — never certain,
/// since name similarity alone can't tell "this row was renamed" from "this is a coincidentally
/// similar but genuinely different row". Icarus's own DataTables are full of systematically-named
/// directional/tier variants — e.g. "..._AngleLeftWWood" vs "..._AngleRightWWood" differ by one
/// word and would sit close together in edit distance despite being different real items — so an
/// unambiguous winner (a real margin over the runner-up) is required before this will ever suggest
/// auto-applying anything.
/// </summary>
public static class StaleItemFixSuggester
{
    private const int MaxCandidateDistance = 3;
    private const int MaxAutoApplyDistance = 2;
    private const int MinRunnerUpMargin = 2;
    private const double MinFieldOverlapForAutoApply = 0.5;

    /// <param name="itemFieldNames">The field names the mod itself defines on this item — used to check the winning candidate actually has somewhere plausible to receive them.</param>
    /// <param name="baseTable">The real base table for the item's own CurrentFile, keyed by row name (from ExmodBaseDiffer's own base-table cache).</param>
    public static StaleItemFixSuggestion? Suggest(string itemName, IEnumerable<string> itemFieldNames, JsonObject baseTable)
    {
        var normalizedTarget = Normalize(itemName);
        var ranked = baseTable
            .Select(kv => kv.Key)
            .Select(candidate => (Name: candidate, Distance: LevenshteinDistance.Compute(normalizedTarget, Normalize(candidate))))
            .Where(c => c.Distance <= MaxCandidateDistance)
            .OrderBy(c => c.Distance)
            .ToList();

        if (ranked.Count == 0)
        {
            return null;
        }

        var best = ranked[0];
        var isUnambiguous = ranked.Count == 1 || ranked[1].Distance - best.Distance >= MinRunnerUpMargin;
        if (!isUnambiguous)
        {
            return new StaleItemFixSuggestion(best.Name, CanAutoApply: false);
        }

        var fieldNames = itemFieldNames as ICollection<string> ?? itemFieldNames.ToList();
        var candidateFields = baseTable[best.Name] as JsonObject;
        var overlapRatio = fieldNames.Count == 0 || candidateFields is null
            ? 0
            : (double)fieldNames.Count(candidateFields.ContainsKey) / fieldNames.Count;

        var canAutoApply = best.Distance <= MaxAutoApplyDistance && overlapRatio >= MinFieldOverlapForAutoApply;
        return new StaleItemFixSuggestion(best.Name, canAutoApply);
    }

    private static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
