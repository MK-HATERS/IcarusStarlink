using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Catches "uncraftable recipe" / "produces nothing" bugs — the natural sibling to
/// ExmodFieldValidityChecker's "is this field name real" question, this asks "does this field's
/// VALUE, when it's a reference to another row, actually point at something real." Confirmed
/// against real Crafting/D_ProcessorRecipes.json: every recipe's Inputs/Outputs (and many other
/// fields elsewhere) use the exact shape {"RowName": "Fiber", "DataTableName": "D_ItemsStatic"} to
/// reference a row in another table by name — a recipe whose Input references a renamed or removed
/// item resolves to nothing at all in-game, not a crash, just a recipe that silently can never
/// actually be crafted (or, for an Output, an item that can never be produced).
///
/// Deliberately narrower than it could be: only a reference that names its own target table
/// explicitly (DataTableName present alongside RowName) is checked — confirmed real and unambiguous
/// (every one of the real Data folder's 298 files has a unique base filename, so a DataTableName
/// resolves to exactly one real file with no guessing). Some references (e.g. a recipe's own
/// "Requirement" or "Audio" field) carry a bare RowName with no DataTableName at all and rely on
/// field-specific implicit context this checker has no reliable way to know — flagging those would
/// risk guessing wrong far more than the explicit ones ever could, so they're silently skipped
/// rather than guessed at.
/// </summary>
public static class ExmodReferenceChecker
{
    public sealed record BrokenReference(string CurrentFile, string ItemName, string FieldPath, string Reason);

    public static IReadOnlyList<BrokenReference> Check(ExmodPackage package, string dataFolder, DataTableRowIndex? index = null)
    {
        index ??= DataTableRowIndex.Build(dataFolder);

        // Confirmed the dominant real false-positive source, not a rare edge case: a mod adding a
        // new craftable item almost always declares BOTH the recipe row (Crafting-D_ProcessorRecipes)
        // AND the item row it outputs (Items-D_ItemTemplate) in the SAME package, and the recipe's
        // own Outputs reference then legitimately points at that self-declared item — real, working
        // content, not a broken reference, since the base game index alone has no way to know about
        // rows a mod is ADDING rather than editing. WithDeclaredRows layers this same mod's own
        // FileItems in as valid targets before checking; it doesn't mutate the shared base index, so
        // it stays safe to reuse index across many mods in one pass.
        var effectiveIndex = index.WithDeclaredRows(package);
        var findings = new List<BrokenReference>();

        foreach (var row in package.Rows)
        {
            // See ExmodSentinelFiles.IsEndOfModMarker's own doc comment.
            if (ExmodSentinelFiles.IsEndOfModMarker(row.CurrentFile))
            {
                continue;
            }

            foreach (var item in row.FileItems)
            {
                foreach (var (fieldName, value) in item.Fields)
                {
                    CheckNode(value, fieldName, row.CurrentFile, item.Name, effectiveIndex, findings);
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Recurses into every object/array a field's value contains — a reference is very rarely the
    /// field's own top-level value (it's normally one layer down inside an "Element" wrapper, or
    /// nested inside an array of recipe Inputs), so a shallow top-level-only check would miss
    /// almost every real reference confirmed in real recipe data.
    /// </summary>
    private static void CheckNode(
        JsonNode? node, string path, string currentFile, string itemName, DataTableRowIndex index, List<BrokenReference> findings)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["RowName"] is JsonValue rowNameValue && rowNameValue.TryGetValue<string>(out var rowName)
                    && obj["DataTableName"] is JsonValue tableNameValue && tableNameValue.TryGetValue<string>(out var tableName)
                    && !string.IsNullOrWhiteSpace(rowName) && !string.IsNullOrWhiteSpace(tableName)
                    // "None" is Unreal's own FName::NAME_None serialization — the DataTable
                    // row-picker's real convention for "no row selected" on an optional reference
                    // field, not a row someone actually named "None". Confirmed real and pervasive
                    // against the user's own 49-mod library: it appears constantly across
                    // completely unrelated mods and tables (D_Talents, D_ModifierStates,
                    // D_StaminaActionCosts) on fields that are clearly meant to be optional
                    // (Requirement, ModifierState, BiomeModifier) — flagging it produced dozens of
                    // false positives per mod before this exclusion existed.
                    && !string.Equals(rowName, "None", StringComparison.OrdinalIgnoreCase))
                {
                    switch (index.Resolve(tableName, rowName))
                    {
                        case ReferenceResolution.TableNotFound:
                            findings.Add(new BrokenReference(currentFile, itemName, path,
                                $"references table '{tableName}', which doesn't exist in the currently-extracted game data."));
                            break;
                        case ReferenceResolution.RowNotFound:
                            findings.Add(new BrokenReference(currentFile, itemName, path,
                                $"references '{rowName}' in {tableName}, but no such row exists — this reference resolves to nothing in-game."));
                            break;
                        case ReferenceResolution.Ok:
                            break;
                    }
                }

                foreach (var (childName, child) in obj)
                {
                    CheckNode(child, $"{path}.{childName}", currentFile, itemName, index, findings);
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    CheckNode(array[i], $"{path}[{i}]", currentFile, itemName, index, findings);
                }
                break;
        }
    }
}

public enum ReferenceResolution { Ok, TableNotFound, RowNotFound }

/// <summary>
/// Row names for every real DataTable file under dataFolder, keyed by each file's own base name
/// (e.g. "D_ItemsStatic") — confirmed real and safe: every one of the 298 real extracted files has
/// a unique base name, so this never needs to disambiguate which folder a DataTableName means.
/// Built once (a full folder walk, ~300 small files) and reused across a whole merge-queue pass the
/// same way ExmodFieldValidityChecker's own schemaCache is, rather than once per mod.
/// </summary>
public sealed class DataTableRowIndex
{
    private readonly Dictionary<string, HashSet<string>> _rowNamesByTable;

    private DataTableRowIndex(Dictionary<string, HashSet<string>> rowNamesByTable) => _rowNamesByTable = rowNamesByTable;

    public static DataTableRowIndex Build(string dataFolder)
    {
        var rowNamesByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dataFolder))
        {
            return new DataTableRowIndex(rowNamesByTable);
        }

        foreach (var filePath in Directory.EnumerateFiles(dataFolder, "*.json", SearchOption.AllDirectories))
        {
            var tableName = Path.GetFileNameWithoutExtension(filePath);
            try
            {
                var fileJson = DuplicateTolerantJson.Parse(File.ReadAllText(filePath))?.AsObject();
                if (fileJson?["Rows"] is not JsonArray rows)
                {
                    continue;
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rowNode in rows)
                {
                    if (rowNode is JsonObject row && row["Name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name))
                    {
                        names.Add(name);
                    }
                }

                rowNamesByTable[tableName] = names;
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                // A single unreadable/malformed real file shouldn't take down the whole index —
                // it's simply treated as "table not found" for any reference pointing at it,
                // matching how a genuinely missing file already behaves.
            }
        }

        return new DataTableRowIndex(rowNamesByTable);
    }

    public ReferenceResolution Resolve(string tableName, string rowName)
    {
        if (!_rowNamesByTable.TryGetValue(tableName, out var names))
        {
            return ReferenceResolution.TableNotFound;
        }

        return names.Contains(rowName) ? ReferenceResolution.Ok : ReferenceResolution.RowNotFound;
    }

    /// <summary>
    /// A new index (this one is left untouched, so it stays safe to reuse across many mods in one
    /// pass) with the given package's own declared rows layered in on top of the real base game
    /// data — a mod adding a new item and referencing it from its own recipe in the same package is
    /// real, working content once that mod is actually merged in, not a broken reference the base
    /// game data alone could ever confirm.
    /// </summary>
    public DataTableRowIndex WithDeclaredRows(ExmodPackage package)
    {
        var copy = _rowNamesByTable.ToDictionary(
            kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        foreach (var row in package.Rows)
        {
            // See ExmodSentinelFiles.IsEndOfModMarker's own doc comment.
            if (ExmodSentinelFiles.IsEndOfModMarker(row.CurrentFile))
            {
                continue;
            }

            // The same CurrentFile -> real relative path convention every other real-data reader in
            // this codebase uses (e.g. BaseDataFileReader) — the table name a reference names is
            // always this file's own base name, e.g. "Items-D_ItemTemplate.json" -> "D_ItemTemplate".
            var tableName = Path.GetFileNameWithoutExtension(row.CurrentFile.Replace('-', '/'));
            if (!copy.TryGetValue(tableName, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                copy[tableName] = names;
            }

            foreach (var item in row.FileItems)
            {
                names.Add(item.Name);
            }
        }

        return new DataTableRowIndex(copy);
    }
}
