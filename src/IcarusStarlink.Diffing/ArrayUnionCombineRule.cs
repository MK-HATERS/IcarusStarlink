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
/// compare against. Two mods adding the same ingredient with different amounts there are an edit
/// of one entry, not two additions, so that also falls through to LastWriteWinsRule.
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
        && group.OrderedChanges.All(c => IsPureAdditionOverBase(baseArray, (JsonArray)c.NewValue!, out _))
        && !TwoModsAddOneThingDifferently(group, baseArray);

    /// <summary>
    /// Keys whose row link names what an entry is about: a recipe's ingredient (Element, Query), a
    /// reward or loot item (Item), a workshop cost (Meta). Two entries naming the same row there are
    /// one ingredient, not two.
    /// </summary>
    private static readonly string[] IdentityKeys = ["Element", "Item", "Query", "Meta"];

    /// <summary>
    /// Two mods adding the same ingredient with different amounts (Wood 10 and Wood 5 on a new
    /// recipe) disagree about one entry: unioned, the recipe would need both (15 wood). Left to
    /// LastWriteWinsRule instead, so it stays a conflict the later mod wins and a pick can change.
    /// Entries that name no row (plain tags, text) never count as the same thing.
    /// </summary>
    private static bool TwoModsAddOneThingDifferently(FieldChangeGroup group, JsonArray baseArray)
    {
        var seen = new Dictionary<string, List<(int Mod, string Text)>>(StringComparer.Ordinal);
        for (var mod = 0; mod < group.OrderedChanges.Count; mod++)
        {
            IsPureAdditionOverBase(baseArray, (JsonArray)group.OrderedChanges[mod].NewValue!, out var additions);
            foreach (var entry in additions)
            {
                if (Identity(entry) is not { } identity)
                {
                    continue;
                }

                var text = JsonText.Canonical(entry);
                if (!seen.TryGetValue(identity, out var others))
                {
                    seen[identity] = others = [];
                }
                else if (others.Any(o => o.Mod != mod && o.Text != text))
                {
                    return true;
                }

                others.Add((mod, text));
            }
        }

        return false;
    }

    /// <summary>"Element:WOOD" for an entry linking a row under one of IdentityKeys; null for anything else.</summary>
    private static string? Identity(JsonNode? entry)
    {
        if (entry is not JsonObject obj)
        {
            return null;
        }

        foreach (var key in IdentityKeys)
        {
            if (obj[key] is JsonObject link && link["RowName"] is JsonValue row && row.TryGetValue<string>(out var name))
            {
                return $"{key}:{name.ToUpperInvariant()}";
            }
        }

        return null;
    }

    /// <summary>
    /// Base's own entries first, every one of them (duplicates included — collapsing a duplicated
    /// base entry would be exactly the "removed" edit this rule promises never to make), then each
    /// mod's additions in queue order. Additions are a multiset union: the same entry added by two
    /// mods is kept once, but a mod that adds an entry twice keeps both copies.
    /// </summary>
    public FieldChange Resolve(FieldChangeGroup group)
    {
        TryGetEffectiveBaseArray(group, out var baseArray);

        var combined = new JsonArray();
        foreach (var baseEntry in baseArray)
        {
            combined.Add(baseEntry?.DeepClone());
        }

        // How many copies of each added entry are already in combined, beyond base's own.
        var placedAdditions = new Dictionary<string, int>();
        foreach (var change in group.OrderedChanges)
        {
            IsPureAdditionOverBase(baseArray, (JsonArray)change.NewValue!, out var additions);
            var seenInThisMod = new Dictionary<string, int>();
            foreach (var entry in additions)
            {
                var text = JsonText.Canonical(entry);
                var copiesInThisMod = seenInThisMod[text] = seenInThisMod.GetValueOrDefault(text) + 1;
                if (copiesInThisMod > placedAdditions.GetValueOrDefault(text))
                {
                    combined.Add(entry?.DeepClone());
                    placedAdditions[text] = copiesInThisMod;
                }
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
    /// as the game reads them (JsonText.Canonical: key order and 2 vs 2.0 don't matter), so a
    /// legitimately duplicated base entry still needs a matching duplicate in the
    /// candidate) with nothing removed or edited — anything left over in candidateArray beyond that
    /// is a genuine addition, returned via additions so a caller doesn't have to recompute it.
    /// </summary>
    private static bool IsPureAdditionOverBase(JsonArray baseArray, JsonArray candidateArray, out List<JsonNode?> additions)
    {
        var remainingBaseCounts = new Dictionary<string, int>();
        foreach (var entry in baseArray)
        {
            var text = JsonText.Canonical(entry);
            remainingBaseCounts[text] = remainingBaseCounts.GetValueOrDefault(text) + 1;
        }

        additions = [];
        foreach (var entry in candidateArray)
        {
            var text = JsonText.Canonical(entry);
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
