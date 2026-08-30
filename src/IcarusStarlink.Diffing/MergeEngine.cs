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
    /// <summary>
    /// baseTablesByFile, when given, is a keyed base-game DataTable lookup (same shape
    /// RebuildService's own ReadBaseTables produces) used to drop a candidate whose value doesn't
    /// actually differ from the CURRENT base game value before resolving/reporting conflicts — see
    /// FindConflicts' own doc comment for why. Passing null skips this (matches every existing
    /// caller's behavior before this parameter existed).
    /// </summary>
    public static IReadOnlyList<FieldChange> Merge(
        IReadOnlyList<IReadOnlyList<FieldChange>> orderedModChanges,
        MergeRuleRegistry registry,
        IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? manualPicks = null,
        IReadOnlyDictionary<string, JsonObject>? baseTablesByFile = null)
    {
        var groups = GroupByField(orderedModChanges, baseTablesByFile);
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
    /// the same GroupByField (with the same baseTablesByFile), so they always produce the same
    /// one-entry-per-mod ordering. See Merge's own doc comment for baseTablesByFile.
    /// </summary>
    public static IReadOnlyList<FieldConflict> FindConflicts(
        IReadOnlyList<string> modNames, IReadOnlyList<IReadOnlyList<FieldChange>> orderedModChanges,
        IReadOnlyDictionary<string, JsonObject>? baseTablesByFile = null)
    {
        if (modNames.Count != orderedModChanges.Count)
        {
            throw new ArgumentException("modNames must have exactly one entry per orderedModChanges entry.", nameof(modNames));
        }

        return [.. GroupByField(orderedModChanges, baseTablesByFile)
            .Select(kv => (kv.Key, Candidates: kv.Value.Select(c => new ConflictCandidate(modNames[c.ModIndex], c.Change)).ToList()))
            .Where(g => g.Candidates.Count > 1
                        && !g.Candidates.All(c => JsonNode.DeepEquals(c.Change.NewValue, g.Candidates[0].Change.NewValue)))
            .Select(g => new FieldConflict(g.Key.CurrentFile, g.Key.ItemName, g.Key.FieldName, g.Candidates))];
    }

    /// <summary>
    /// For every mod named in any conflict's own Candidates, the set of every OTHER mod it shares
    /// at least one conflicting field with — aggregated across all conflicts, since two mods can
    /// disagree on more than one field, and a mod can conflict with different mods on different
    /// fields. Built for a per-row "which mods does this one conflict with" queue indicator, so a
    /// user doesn't need to open the full conflict picker just to see that a row is involved in one.
    /// Keyed by ConflictCandidate.ModName — display text, not a folder identifier, matching
    /// FieldConflict's own established convention (see its doc comment).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GroupConflictsByMod(IReadOnlyList<FieldConflict> conflicts)
    {
        var byMod = new Dictionary<string, HashSet<string>>();
        foreach (var conflict in conflicts)
        {
            foreach (var candidate in conflict.Candidates)
            {
                if (!byMod.TryGetValue(candidate.ModName, out var others))
                {
                    others = [];
                    byMod[candidate.ModName] = others;
                }

                foreach (var other in conflict.Candidates)
                {
                    if (other.ModName != candidate.ModName)
                    {
                        others.Add(other.ModName);
                    }
                }
            }
        }

        return byMod.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList());
    }

    /// <summary>
    /// How many of one mod's own field changes actually differ from the current base game value —
    /// used by the "suggest queue order" heuristic to tell a mod making a few real, deliberate
    /// edits apart from one whose EXMOD carries many unchanged, stale copied fields. See
    /// GroupByField's own doc comment: an old-style extractor pulled whole items, so a mod's raw
    /// field count alone overstates how much it actually changes — comparing each field against
    /// real base data first is what makes this a genuine signal instead of a guess. Collapses the
    /// same (file, item, field) touched more than once by this one mod down to its last value
    /// first, matching GroupByField's own per-mod dedup rule (see its doc comment for why).
    /// </summary>
    public static int CountChangesDifferingFromBase(
        IReadOnlyList<FieldChange> modChanges, IReadOnlyDictionary<string, JsonObject> baseTablesByFile)
    {
        var lastByKey = new Dictionary<(string CurrentFile, string ItemName, string FieldName), FieldChange>(FieldChangeKeyComparer.Instance);
        foreach (var change in modChanges)
        {
            lastByKey[(change.CurrentFile, change.ItemName, change.FieldName)] = change;
        }

        var count = 0;
        foreach (var (key, change) in lastByKey)
        {
            var (found, baseValue) = TryGetBaseValue(baseTablesByFile, key.CurrentFile, key.ItemName, key.FieldName);
            if (!found || !JsonNode.DeepEquals(change.NewValue, baseValue))
            {
                count++;
            }
        }

        return count;
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
    ///
    /// When baseTablesByFile is given, a candidate whose value doesn't actually differ from the
    /// CURRENT base game value is dropped from its group entirely (and the group removed if nothing
    /// is left) — learned from a real ~5,000-item library survey: 22% of real field-level changes
    /// carry 4+ fields, strongly suggesting many are whole-row-copy artifacts (classic IMM's own
    /// changelog documents its original extractor doing exactly this before a per-field rewrite),
    /// not a deliberate edit. Left in place, a mod's own stale copy of a field it never meant to
    /// touch could silently out-rank (or falsely appear to "conflict" with) another mod's genuine
    /// edit, purely because of queue position. The one accepted trade-off: a mod that deliberately
    /// sets a field BACK to its base value to override an earlier mod's edit gets filtered the same
    /// way, since nothing in the data can tell "stale copy" apart from "deliberate revert" — judged
    /// the right call given how much more common the former is in real data.
    /// </summary>
    private static Dictionary<(string CurrentFile, string ItemName, string FieldName), List<(int ModIndex, FieldChange Change)>> GroupByField(
        IReadOnlyList<IReadOnlyList<FieldChange>> orderedModChanges, IReadOnlyDictionary<string, JsonObject>? baseTablesByFile)
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

        if (baseTablesByFile is not null)
        {
            foreach (var key in groups.Keys.ToList())
            {
                var (found, baseValue) = TryGetBaseValue(baseTablesByFile, key.CurrentFile, key.ItemName, key.FieldName);
                if (!found)
                {
                    // No base value to compare against (a genuinely new item, this file/item isn't
                    // in the base tables provided, or the field is genuinely absent from base) —
                    // nothing to filter. Distinct from "found, and it's a real JSON null" below —
                    // collapsing the two would wrongly skip filtering a stale candidate whose own
                    // copied value is also null.
                    continue;
                }

                groups[key].RemoveAll(c => JsonNode.DeepEquals(c.Change.NewValue, baseValue));
                if (groups[key].Count == 0)
                {
                    groups.Remove(key);
                }
            }
        }

        return groups;
    }

    private static (bool Found, JsonNode? Value) TryGetBaseValue(IReadOnlyDictionary<string, JsonObject> baseTablesByFile, string currentFile, string itemName, string fieldName)
    {
        if (!baseTablesByFile.TryGetValue(currentFile, out var table) || table[itemName] is not JsonObject item || !item.ContainsKey(fieldName))
        {
            return (false, null);
        }

        return (true, item.TryGetPropertyValue(fieldName, out var value) ? value : null);
    }
}
