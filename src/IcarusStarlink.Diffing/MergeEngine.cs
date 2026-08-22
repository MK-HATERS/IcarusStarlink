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
        var groups = new Dictionary<(string CurrentFile, string ItemName, string FieldName), List<FieldChange>>(FieldChangeKeyComparer.Instance);

        foreach (var modChanges in orderedModChanges)
        {
            foreach (var change in modChanges)
            {
                var key = (change.CurrentFile, change.ItemName, change.FieldName);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }

                list.Add(change);
            }
        }

        var resolved = new List<FieldChange>(groups.Count);

        foreach (var (key, changes) in groups)
        {
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
    /// Finds every field two or more of orderedModChanges' mods touch with genuinely different
    /// values — the set a human might want to review before Rebuild, via the advanced conflict
    /// picker Merge's own manualPicks parameter feeds into. A field only both mods happen to set to
    /// the identical value isn't included: there's nothing to pick between. modNames must be the
    /// same length as orderedModChanges, in the same queue order (index 0 = lowest priority) — each
    /// returned FieldConflict.Candidates is built by walking both lists together, so a picked
    /// Candidates[i] lines up with the pickedIndex Merge itself expects only when Merge is later
    /// called with this exact same orderedModChanges (same mods, same order, same queue snapshot).
    /// </summary>
    public static IReadOnlyList<FieldConflict> FindConflicts(
        IReadOnlyList<string> modNames, IReadOnlyList<IReadOnlyList<FieldChange>> orderedModChanges)
    {
        if (modNames.Count != orderedModChanges.Count)
        {
            throw new ArgumentException("modNames must have exactly one entry per orderedModChanges entry.", nameof(modNames));
        }

        var groups = new Dictionary<(string CurrentFile, string ItemName, string FieldName), List<ConflictCandidate>>(FieldChangeKeyComparer.Instance);

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

                list.Add(new ConflictCandidate(modNames[i], change));
            }
        }

        return [.. groups
            .Where(kv => kv.Value.Count > 1 && !kv.Value.All(c => JsonNode.DeepEquals(c.Change.NewValue, kv.Value[0].Change.NewValue)))
            .Select(kv => new FieldConflict(kv.Key.CurrentFile, kv.Key.ItemName, kv.Key.FieldName, kv.Value))];
    }
}
