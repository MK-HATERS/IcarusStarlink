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
    public static IReadOnlyList<FieldChange> DiffAgainstBase(
        ExmodPackage package, string dataFolder, ISemanticClassifier classifier, MergeReport? report = null)
    {
        var changes = new List<FieldChange>();

        foreach (var row in package.Rows)
        {
            // Same convention confirmed throughout Phase 6: "Traits-D_Fuel.json" -> "Traits/D_Fuel.json".
            var realRelativePath = row.CurrentFile.Replace('-', '/');
            var basePath = Path.Combine(dataFolder, realRelativePath);
            if (!File.Exists(basePath))
            {
                report?.AddWarning($"No matching base file for '{row.CurrentFile}' at '{realRelativePath}'.");
                continue;
            }

            var baseFileJson = JsonNode.Parse(File.ReadAllText(basePath))!.AsObject();
            var baseKeyed = DataTableJson.RowsToKeyedObject(baseFileJson);
            var moddedKeyed = ToKeyedObject(row);

            changes.AddRange(TableDiffer.Diff(baseKeyed, moddedKeyed, row.CurrentFile, classifier, report));
        }

        return changes;
    }

    /// <summary>
    /// A row's own FileItems are already exactly the sparse "modded side" shape TableDiffer.Diff
    /// expects — deep-cloning each field value first, since JsonNode instances from the already-
    /// parsed package are still attached to that package's own tree and a JsonNode can only ever
    /// belong to one parent.
    /// </summary>
    private static JsonObject ToKeyedObject(ExmodFileRow row)
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
