using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>
/// Two mods that each ADD different entries to the same JSON array field (recipe Inputs/Outputs,
/// loot tables, ...) should keep both mods' own additions, not have one silently overwrite the
/// other the way a plain scalar field would — the same "combine instead of overwrite" idea
/// GameplayTagQueryCombineRule already established for gameplay-tag-query fields, generalized here
/// to any array-valued GenericCompound/RowReference field (see DefaultSemanticClassifier's own
/// classification: an array value that isn't a gameplay-tag-query-named field classifies as
/// GenericCompound today; RowReference is accepted too in case a future classifier change ever
/// produces an array-shaped RowReference change, or a caller constructs one directly).
///
/// Deliberately narrower than its sibling: blindly unioning here is riskier than for a tag query —
/// a recipe's own Inputs list has real per-entry meaning, and removing or editing an entry is a
/// deliberate balance decision, not something to silently ignore the way a duplicate tag entry can
/// be. So this only fires when EVERY mod's own array is a strict superset-add over the live base
/// game array for that field: its own array contains every one of base's own entries, unchanged,
/// plus some additional entries — nothing removed, nothing edited. The moment any one mod's array
/// doesn't cleanly decompose that way, Applies returns false and the field falls through to the
/// registry's normal LastWriteWinsRule fallback, which still leaves it as a real, visible conflict
/// for MergeEngine.FindConflicts/the manual picker to surface — this rule never guesses which edit
/// should win.
///
/// No live base value at all (FieldChangeGroup.HasBaseValue false — a brand-new item, or a field
/// genuinely absent from the base row) is treated as an empty base array: every mod's own entries
/// are then, by definition, pure additions over nothing, so this still safely unions them. This is
/// in fact the most common real case this rule exists to fix — two mods each adding different
/// entries to a brand-new recipe's own Inputs/Outputs list, where there's no base array at all to
/// compare against.
/// </summary>
public sealed class ArrayUnionCombineRule : IFieldMergeRule
{
    public bool Applies(FieldChangeGroup group) =>
        // Only kick in for an actual multi-mod conflict — a lone mod's change should pass through
        // LastWriteWinsRule untouched, matching GameplayTagQueryCombineRule's own convention.
        group.OrderedChanges.Count > 1
        && group.OrderedChanges.All(c => c.Semantic is ValueSemantic.GenericCompound or ValueSemantic.RowReference)
        && group.OrderedChanges.All(c => !c.IsFieldRemoved)
        && group.OrderedChanges.All(c => c.NewValue is JsonArray)
        && TryGetEffectiveBaseArray(group, out var baseArray)
        && group.OrderedChanges.All(c => IsPureAdditionOverBase(baseArray, (JsonArray)c.NewValue!, out _));

    public FieldChange Resolve(FieldChangeGroup group)
    {
        TryGetEffectiveBaseArray(group, out var baseArray);

        var combined = new JsonArray();
        var seen = new HashSet<string>();

        void AddIfNew(JsonNode? entry)
        {
            var text = entry?.ToJsonString() ?? "null";
            if (seen.Add(text))
            {
                combined.Add(entry?.DeepClone());
            }
        }

        foreach (var baseEntry in baseArray)
        {
            AddIfNew(baseEntry);
        }

        foreach (var change in group.OrderedChanges)
        {
            foreach (var entry in (JsonArray)change.NewValue!)
            {
                AddIfNew(entry);
            }
        }

        return group.OrderedChanges[^1] with { NewValue = combined };
    }

    /// <summary>
    /// The base array to compare candidates against — a real base JsonArray value as-is, or an
    /// empty array standing in for "no base value known" (see this class's own doc comment for why
    /// that's safe here). Returns false only when a real base value exists but genuinely isn't an
    /// array (a malformed/mismatched base table this rule has no safe way to reason about) — Applies
    /// then correctly declines rather than guessing.
    /// </summary>
    private static bool TryGetEffectiveBaseArray(FieldChangeGroup group, out JsonArray baseArray)
    {
        if (!group.HasBaseValue || group.BaseValue is null)
        {
            baseArray = [];
            return true;
        }

        if (group.BaseValue is JsonArray array)
        {
            baseArray = array;
            return true;
        }

        baseArray = [];
        return false;
    }

    /// <summary>
    /// True when candidateArray contains every one of baseArray's own entries (matched one-for-one
    /// by JSON text, so a legitimately duplicated base entry still needs a matching duplicate in the
    /// candidate) with nothing removed or edited — anything left over in candidateArray beyond that
    /// is a genuine addition, returned via additions so a caller doesn't have to recompute it.
    /// </summary>
    private static bool IsPureAdditionOverBase(JsonArray baseArray, JsonArray candidateArray, out List<JsonNode?> additions)
    {
        var remainingBaseCounts = new Dictionary<string, int>();
        foreach (var entry in baseArray)
        {
            var text = entry?.ToJsonString() ?? "null";
            remainingBaseCounts[text] = remainingBaseCounts.GetValueOrDefault(text) + 1;
        }

        additions = [];
        foreach (var entry in candidateArray)
        {
            var text = entry?.ToJsonString() ?? "null";
            if (remainingBaseCounts.TryGetValue(text, out var count) && count > 0)
            {
                remainingBaseCounts[text] = count - 1;
            }
            else
            {
                additions.Add(entry);
            }
        }

        // Any leftover count means a base entry never shows up (unchanged) in the candidate's own
        // array at all — removed outright, or edited into something no longer textually identical.
        return remainingBaseCounts.Values.All(count => count == 0);
    }
}
