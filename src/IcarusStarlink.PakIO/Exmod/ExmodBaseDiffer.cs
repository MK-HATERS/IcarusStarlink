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
            foreach (var (fieldName, value) in item.Fields)
            {
                fields[fieldName] = value?.DeepClone();
            }
            keyed[item.Name] = fields;
        }
        return keyed;
    }
}
