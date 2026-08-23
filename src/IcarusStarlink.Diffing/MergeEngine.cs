using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>
/// Combines an ordered queue of mods' changes (index 0 = lowest priority) into one resolved
/// change list, one field at a time. manualPicks lets a caller override the registry's automatic
/// resolution for specific fields — this is the "advanced conflict picker": an index into that
/// field's ordered candidate list, not a boolean earlier/later flag, so it still works when 3+
/// mods touch the same field.
/// </summary>
public static class MergeEngine
{
    public static IReadOnlyList<FieldChange> Merge(
        IReadOnlyList<IReadOnlyList<FieldChange>> orderedModChanges,
        MergeRuleRegistry registry,
        IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? manualPicks = null)
    {
        var groups = GroupByField(orderedModChanges);
        var resolved = new List<FieldChange>(groups.Count);

        foreach (var (key, perMod) in groups)
        {
            var changes = perMod.Select(c => c.Change).ToList();
            if (manualPicks is not null && manualPicks.TryGetValue(key, out var pickedIndex))
            {
                if (pickedIndex < 0 || pickedIndex >= changes.Count)
                {
                    // Most likely cause: the pick was made before a later queue reorder/removal
                    // changed how many mods now touch this field. Surface that clearly instead
                    // of an IndexOutOfRangeException or silently applying the wrong mod's value.
                    throw new ArgumentOutOfRangeException(
                        nameof(manualPicks),
                        pickedIndex,
                        $"Manual pick for {key.CurrentFile}:{key.ItemName}.{key.FieldName} is out of range " +
                        $"— {changes.Count} mod(s) currently touch this field. The pick is likely stale.");
                }

                resolved.Add(changes[pickedIndex]);
                continue;
            }

            resolved.Add(registry.Resolve(new FieldChangeGroup(key.CurrentFile, key.ItemName, key.FieldName, changes)));
        }

        return resolved;
    }

    /// <summary>
    /// Finds every field two or more DIFFERENT mods touch with genuinely different values — the set
    /// a human might want to review before Rebuild, via the advanced conflict picker Merge's own
    /// manualPicks parameter feeds into. A field several mods happen to set to the identical value
    /// isn't included: there's nothing to pick between. modNames must be the same length as
    /// orderedModChanges, in the same queue order (index 0 = lowest priority) — a picked
    /// Candidates[i] lines up with the pickedIndex Merge expects because both methods group through
    /// the same GroupByField, so they always produce the same one-entry-per-mod ordering.
    /// </summary>
    public static IReadOnlyList<FieldConflict> FindConflicts(
        IReadOnlyList<string> modNames, IReadOnlyList<IReadOnlyList<FieldChange>> orderedModChanges)
    {
        if (modNames.Count != orderedModChanges.Count)
        {
            throw new ArgumentException("modNames must have exactly one entry per orderedModChanges entry.", nameof(modNames));
        }

        return [.. GroupByField(orderedModChanges)
            .Select(kv => (kv.Key, Candidates: kv.Value.Select(c => new ConflictCandidate(modNames[c.ModIndex], c.Change)).ToList()))
            .Where(g => g.Candidates.Count > 1
                        && !g.Candidates.All(c => JsonNode.DeepEquals(c.Change.NewValue, g.Candidates[0].Change.NewValue)))
            .Select(g => new FieldConflict(g.Key.CurrentFile, g.Key.ItemName, g.Key.FieldName, g.Candidates))];
    }

    /// <summary>
    /// Groups every mod's changes by (file, item, field), keeping AT MOST ONE entry per mod — the
    /// last value that mod itself supplies for that field.
    ///
    /// The one-per-mod part matters, and was learned from real mod data: a single .EXMOD's
    /// File_Items list can name the same item more than once (a real example — laanp-ExtraDeployables
    /// lists Prop_PaperTowels twice, with different recipe Inputs). Those duplicates are not a merge
    /// conflict anyone can resolve — no user can "pick between" one mod and itself — and the merged
    /// output only ever holds one value per field anyway, since TableApplier assigns
    /// [item][field] and the last assignment wins. Collapsing here makes that explicit, keeps the
    /// conflict picker showing only genuine mod-vs-mod disagreements, and — because Merge and
    /// FindConflicts both group through this one method — guarantees a picked candidate index still
    /// means the same thing to both.
    /// </summary>
    private static Dictionary<(string CurrentFile, string ItemName, string FieldName), List<(int ModIndex, FieldChange Change)>> GroupByField(
        IReadOnlyList<IReadOnlyList<FieldChange>> orderedModChanges)
    {
        var groups = new Dictionary<(string CurrentFile, string ItemName, string FieldName), List<(int ModIndex, FieldChange Change)>>(FieldChangeKeyComparer.Instance);

        for (var i = 0; i < orderedModChanges.Count; i++)
        {
            foreach (var change in orderedModChanges[i])
            {
                var key = (change.CurrentFile, change.ItemName, change.FieldName);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }

                // Same mod touching this field again: replace its own earlier entry rather than
                // adding a second candidate for it, preserving its position in queue order.
                var existingIndex = list.FindIndex(c => c.ModIndex == i);
                if (existingIndex >= 0)
                {
                    list[existingIndex] = (i, change);
                }
                else
                {
                    list.Add((i, change));
                }
            }
        }

        return groups;
    }
}
