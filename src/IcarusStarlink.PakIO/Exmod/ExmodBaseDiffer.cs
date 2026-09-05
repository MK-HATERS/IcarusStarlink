using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// "Side-by-side original game data with amber highlights on differences" per the spec — unlike
/// ExmodFieldChangeMapper.ToFieldChanges (which always leaves FieldChange.OriginalValue null,
/// since EXMOD itself never records the base value), this reads the real base file per CurrentFile
/// and reuses TableDiffer.Diff (Phase 1) to populate real OriginalValues, for the EXMOD editor's
/// own amber-highlight rendering.
/// </summary>
public static class ExmodBaseDiffer
{
    /// <param name="baseTableCache">
    /// Optional, keyed by CurrentFile — lets a whole-library pass (checking many mods that often
    /// touch the same real file, e.g. Traits-D_Itemable.json) parse each base file once instead of
    /// once per mod. Null (the default, and every call site before this) parses fresh every time,
    /// unchanged from the original behavior. A cache hit still re-reports a missing base file on
    /// every call, since that warning is a per-mod signal, not something the cache itself should
    /// suppress.
    /// </param>
    public static IReadOnlyList<FieldChange> DiffAgainstBase(
        ExmodPackage package, string dataFolder, ISemanticClassifier classifier, MergeReport? report = null,
        IDictionary<string, JsonObject?>? baseTableCache = null)
    {
        var changes = new List<FieldChange>();

        foreach (var row in package.Rows)
        {
            // See ExmodSentinelFiles.IsEndOfModMarker's own doc comment — diffing it against base
            // game data can only ever fail (there is no "EndOfMod" table), producing a "no matching
            // base file" warning that reads as a real problem with the mod when it's actually
            // universal and harmless.
            if (ExmodSentinelFiles.IsEndOfModMarker(row.CurrentFile))
            {
                continue;
            }

            var baseKeyed = ResolveBaseTable(row.CurrentFile, dataFolder, report, baseTableCache);
            if (baseKeyed is null)
            {
                continue;
            }

            var moddedKeyed = ToKeyedObject(row);
            changes.AddRange(TableDiffer.Diff(baseKeyed, moddedKeyed, row.CurrentFile, classifier, report));
        }

        return changes;
    }

    /// <summary>
    /// Builds a copy of package.Rows with every field that ended up identical to the real base game
    /// value stripped out — meant to run right before writing an EXMOD to disk, so a saved mod only
    /// ever carries genuine deltas instead of e.g. every untouched field "Add item from game data"
    /// copies in wholesale (it clones the item's COMPLETE real row, then the user typically edits
    /// only one or two fields — every other field stays physically present, byte-identical to base,
    /// unless something like this strips it back out).
    ///
    /// Deliberately NOT built on DiffAgainstBase/TableDiffer.Diff, even though this file is exactly
    /// where that logic already lives: TableDiffer.Diff treats a field present in the base row but
    /// ABSENT from the modded side as an explicit removal — correct when diffing a COMPLETE compiled
    /// DataTable file against base (e.g. a prebuilt-pak import, where "modded" really is the whole
    /// table), but the OPPOSITE of EXMOD's own real, sparse on-disk convention: a field an item
    /// simply doesn't list means "leave it alone," never "delete it" (confirmed:
    /// ExmodFieldChangeMapper.ToFieldChanges only ever emits a FieldChange for a field actually
    /// present in item.Fields, never inventing a removal for one merely absent — and AddItem/
    /// AddField together are a real, intentional editor workflow for hand-authoring exactly this
    /// kind of sparse item, e.g. adding a single MaxStack override to an item name that happens to
    /// match a real base row, without copying that row's other fields in at all). Reusing
    /// TableDiffer.Diff here would silently reinterpret that whole class of deliberately-sparse item
    /// as "delete every other field of this row" the moment the mod is saved. So this only ever
    /// iterates fields an item ALREADY explicitly lists in its own Fields dictionary, and only ever
    /// strips one that turns out to be genuinely identical to base; it never adds a change for a
    /// field the item doesn't mention at all, and it never drops a row or an item outright (even one
    /// that ends up with zero surviving fields) — an empty file/item placeholder a user is still in
    /// the middle of populating (Insert file at location / Add item, before adding any fields yet)
    /// must survive a Save exactly as before.
    /// </summary>
    public static List<ExmodFileRow> StripFieldsIdenticalToBase(ExmodPackage package, string dataFolder, MergeReport? report = null)
    {
        var result = new List<ExmodFileRow>();

        foreach (var row in package.Rows)
        {
            // Same reasoning as DiffAgainstBase's own EndOfMod skip, and the same "no real base file
            // to safely compare against" fallback PrebuiltPakToExmodConverter already establishes for
            // content that can't be diffed: keep the row completely as-is rather than risk silently
            // dropping real content a genuinely new custom table, a stale/unset data folder, or a
            // path-safety rejection would otherwise cause DiffAgainstBase to silently contribute
            // nothing for.
            if (ExmodSentinelFiles.IsEndOfModMarker(row.CurrentFile))
            {
                result.Add(row);
                continue;
            }

            var baseKeyed = BaseDataFileReader.ReadKeyedTable(dataFolder, row.CurrentFile, report);
            if (baseKeyed is null)
            {
                result.Add(row);
                continue;
            }

            var strippedItems = new List<ExmodFileItem>();
            foreach (var item in row.FileItems)
            {
                var baseRow = baseKeyed[item.Name] as JsonObject;
                var strippedFields = new Dictionary<string, JsonNode?>();

                // Snapshot, not a live enumeration — same reasoning as ToKeyedObject's own .ToList()
                // just below: item.Fields is a plain mutable Dictionary the editor writes into on
                // every keystroke.
                foreach (var (fieldName, value) in item.Fields.ToList())
                {
                    var baseHasField = baseRow?.ContainsKey(fieldName) ?? false;
                    var baseValue = baseHasField ? baseRow![fieldName] : null;
                    // An explicit "removed" field (value is null, matching FromFieldChanges' own
                    // round-trip convention) that base never had anyway is a pure no-op — safe to
                    // strip even though baseHasField is false, unlike a genuinely new non-null value.
                    var isRedundantRemoval = !baseHasField && value is null;

                    if ((baseHasField && JsonNode.DeepEquals(baseValue, value)) || isRedundantRemoval)
                    {
                        continue;
                    }

                    strippedFields[fieldName] = value?.DeepClone();
                }

                strippedItems.Add(new ExmodFileItem { Name = item.Name, Fields = strippedFields });
            }

            result.Add(new ExmodFileRow { CurrentFile = row.CurrentFile, FileItems = strippedItems });
        }

        return result;
    }

    private static JsonObject? ResolveBaseTable(
        string currentFile, string dataFolder, MergeReport? report, IDictionary<string, JsonObject?>? baseTableCache)
    {
        if (baseTableCache is not null && baseTableCache.TryGetValue(currentFile, out var cached))
        {
            if (cached is null)
            {
                report?.AddWarning($"No matching base file for '{currentFile}' at '{currentFile.Replace('-', '/')}'.");
            }

            return cached;
        }

        var baseKeyed = BaseDataFileReader.ReadKeyedTable(dataFolder, currentFile, report);
        baseTableCache?.Add(currentFile, baseKeyed);
        return baseKeyed;
    }

    /// <summary>
    /// A row's own FileItems are already exactly the sparse "modded side" shape TableDiffer.Diff
    /// expects — deep-cloning each field value first, since JsonNode instances from the already-
    /// parsed package are still attached to that package's own tree and a JsonNode can only ever
    /// belong to one parent. Internal (not private): ModVersionComparer.ToKeyedTablesByFile reuses
    /// this same per-row transform rather than re-deriving it, since the two are the identical
    /// "sparse EXMOD row → TableDiffer-shaped JsonObject" rule.
    ///
    /// Two FileItems sharing the same Name is possible in real EXMOD content — the plain indexer
    /// assignment below (keyed[item.Name] = fields) means the last one simply wins, deliberately:
    /// the same last-one-wins convention DataTableJson.RowsToKeyedObject applies to a duplicate row
    /// name, and MergeEngine.GroupByField applies to one mod touching the same field twice — not a
    /// gap to close here.
    /// </summary>
    internal static JsonObject ToKeyedObject(ExmodFileRow row)
    {
        var keyed = new JsonObject();
        foreach (var item in row.FileItems)
        {
            var fields = new JsonObject();
            // Snapshot, not a live enumeration — this runs on essentially every edit (RefreshBaseDiff
            // is called after AddField/RemoveField/AddItem/etc.), so it's an even more likely site
            // than ExmodJson.RowToJsonObject for the same real class of bug: the editor writes each
            // keystroke straight into this same Dictionary on the UI thread, and iterating it live
            // here risks corrupting its internal state if a field edit's own binding update is still
            // in-flight. See RowToJsonObject's own comment for the real crash this class of bug
            // produced ("Operations that change non-concurrent collections must have exclusive
            // access...").
            foreach (var (fieldName, value) in item.Fields.ToList())
            {
                fields[fieldName] = value?.DeepClone();
            }
            keyed[item.Name] = fields;
        }
        return keyed;
    }
}
