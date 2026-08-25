using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// "Read one real base-game DataTable file, keyed by row name" — the exact sequence ExmodBaseDiffer,
/// RebuildService, and GameplayOptionsFieldChangeGenerator each used to reimplement independently:
/// resolve CurrentFile ("Traits-D_Fuel.json") to its real extracted-data relative path
/// ("Traits/D_Fuel.json"), read it, parse it, and key it via DataTableJson.RowsToKeyedObject. Shared
/// here so the file-resolution convention and warning wording can't drift between call sites, and so
/// every caller gets DuplicateTolerantJson's crash tolerance for free — a real base file with a
/// duplicate JSON key (confirmed to happen; see DuplicateTolerantJson's own doc comment) would
/// otherwise throw ArgumentException from a plain JsonNode.Parse and abort whatever pipeline hit it.
/// </summary>
internal static class BaseDataFileReader
{
    /// <summary>Returns null (with a report warning) if no base file exists at the resolved path.</summary>
    public static JsonObject? ReadKeyedTable(string dataFolder, string currentFile, MergeReport? report)
    {
        var fileJson = ParseFile(dataFolder, currentFile, report);
        if (fileJson is null)
        {
            return null;
        }

        return DataTableJson.RowsToKeyedObject(fileJson, duplicateName => report?.AddWarning(
            $"'{currentFile}' has more than one row named '{duplicateName}' — only the last one was kept."));
    }

    /// <summary>
    /// Lower-level counterpart to ReadKeyedTable, for a caller (RebuildService) that also needs the
    /// original unkeyed file — e.g. to preserve its RowStruct/Defaults when writing a merged table
    /// back out. Resolves and parses only; does not key by row name.
    /// </summary>
    public static JsonObject? ParseFile(string dataFolder, string currentFile, MergeReport? report)
    {
        var realRelativePath = currentFile.Replace('-', '/');
        var basePath = Path.Combine(dataFolder, realRelativePath);
        if (!File.Exists(basePath))
        {
            report?.AddWarning($"No matching base file for '{currentFile}' at '{realRelativePath}'.");
            return null;
        }

        return DuplicateTolerantJson.Parse(File.ReadAllText(basePath))!.AsObject();
    }
}
