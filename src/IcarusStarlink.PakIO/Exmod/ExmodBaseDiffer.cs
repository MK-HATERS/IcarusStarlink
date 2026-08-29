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
            // A real, universal terminator marker — confirmed present at the end of every one of
            // dozens of real EXMOD files inspected, always with an empty File_Items — not a game
            // file reference at all, just a "this is the end of the mod" sentinel every known
            // extraction tool appends. Diffing it against base game data can only ever fail (there
            // is no "EndOfMod" table), producing a "no matching base file" warning that reads as a
            // real problem with the mod when it's actually universal and harmless.
            if (string.Equals(row.CurrentFile, "EndOfMod", StringComparison.OrdinalIgnoreCase))
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
