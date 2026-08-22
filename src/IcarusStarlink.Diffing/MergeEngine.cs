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
}
