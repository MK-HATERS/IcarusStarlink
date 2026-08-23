using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Reads/writes the .EXMOD JSON shape by walking a JsonNode tree directly rather than relying on
/// System.Text.Json's POCO attribute mapping — File_Items entries mix a known "Name" key with
/// arbitrary dynamic field keys, which doesn't fit an attribute-driven contract cleanly, and this
/// keeps everything in the same JsonNode world TableDiffer/TableApplier already operate in.
///
/// Parse() also enforces the fileName path-safety check (via AssetPathGuard) rather than leaving
/// that purely to the Container layer's writers: fileName is untrusted content the moment it's
/// parsed, so rejecting an unsafe one here — at the earliest point it exists as a value at all —
/// beats deferring it to whichever write path happens to run later.
/// </summary>
public static class ExmodJson
{
    public static ExmodPackage Parse(string json)
    {
        // Duplicate-tolerant rather than JsonNode.Parse: real mods in the wild repeat a key inside
        // one object, and rejecting those made a working mod completely unusable here (see
        // DuplicateTolerantJson). Last occurrence wins, matching normal JSON semantics.
        var root = DuplicateTolerantJson.Parse(json) as JsonObject
            ?? throw new FormatException("EXMOD JSON root is not an object.");
        return Parse(root);
    }

    public static ExmodPackage Parse(JsonObject root)
    {
        try
        {
            return ParseCore(root);
        }
        catch (ArgumentException ex)
        {
            // JsonObject lazily builds a property dictionary per object node and throws
            // ArgumentException the first time a node with a real duplicate JSON key (same key
            // twice in one {} block) is queried — observed in the wild in a real Jimk72-authored
            // EXMOD file where a single File_Item's own object listed "ResourceCostMultipliers"
            // twice. That's malformed EXMOD content exactly like every other case in this parser,
            // so it should surface as the same FormatException callers already expect (and that
            // FolderLibraryRepository.RescanAll's skip-and-log path already handles), not a raw
            // framework exception type nothing downstream is prepared for.
            throw new FormatException("EXMOD JSON contains a duplicate key within one object.", ex);
        }
    }

    private static ExmodPackage ParseCore(JsonObject root)
    {
        var fileName = GetRequiredString(root, "fileName");
        AssetPathGuard.EnsureSimpleFileName(fileName);

        var package = new ExmodPackage
        {
            Name = GetRequiredString(root, "name"),
            Author = GetRequiredString(root, "author"),
            Version = GetRequiredString(root, "version"),
            Description = GetRequiredString(root, "description"),
            FileName = fileName,
            ImageUrl = GetString(root, "imageURL"),
            ReadmeUrl = GetString(root, "readmeURL"),
            Level2 = GetString(root, "Level2"),
            Week = GetString(root, "week"),
            VariantGroup = GetString(root, "variantGroup"),
            Variant = GetString(root, "variant"),
            VariantSort = GetInt(root, "variantSort"),
        };

        if (root["Rows"] is JsonArray rowsArray)
        {
            foreach (var rowNode in rowsArray)
            {
                if (rowNode is not JsonObject rowObj)
                {
                    // Every other malformed field in this parser throws rather than silently
                    // dropping data — a non-object Rows entry shouldn't be the one exception.
                    throw new FormatException("EXMOD JSON 'Rows' array contains a non-object entry.");
                }

                package.Rows.Add(ParseRow(rowObj));
            }
        }

        return package;
    }

    /// <summary>
    /// One Rows entry: {"CurrentFile": ..., "File_Items": [...]}. Extracted out of ParseCore's own
    /// loop (Phase 7.2) so the EXMOD editor's "File JSON" raw view — one file's worth of a mod's
    /// changes, edited as text — can parse/reserialize a single row through the exact same
    /// validation ParseCore/ToJsonObject already apply to every row, rather than a laxer path.
    /// </summary>
    public static ExmodFileRow ParseRow(JsonObject rowObj)
    {
        var currentFile = GetRequiredString(rowObj, "CurrentFile");
        EnsurePlainIdentifier(currentFile, "'CurrentFile'");
        var row = new ExmodFileRow { CurrentFile = currentFile };

        if (rowObj["File_Items"] is JsonArray itemsArray)
        {
            foreach (var itemNode in itemsArray)
            {
                if (itemNode is not JsonObject itemObj)
                {
                    throw new FormatException(
                        $"EXMOD JSON 'File_Items' array (in '{currentFile}') contains a non-object entry.");
                }

                var itemName = GetRequiredString(itemObj, "Name");
                EnsurePlainIdentifier(itemName, "item 'Name'");
                var item = new ExmodFileItem { Name = itemName };
                foreach (var (key, value) in itemObj)
                {
                    if (key == "Name")
                    {
                        continue;
                    }

                    item.Fields[key] = value?.DeepClone();
                }

                row.FileItems.Add(item);
            }
        }

        return row;
    }

    public static JsonObject ToJsonObject(ExmodPackage package)
    {
        var root = new JsonObject
        {
            ["name"] = package.Name,
            ["author"] = package.Author,
            ["version"] = package.Version,
            ["description"] = package.Description,
            ["fileName"] = package.FileName,
            // Unlike Level2/week/variant* below, both real samples always carried these two keys
            // present-but-empty rather than omitting them — so, deliberately unlike the other
            // optional fields, null here maps to "" rather than being omitted, to match that.
            ["imageURL"] = package.ImageUrl ?? "",
            ["readmeURL"] = package.ReadmeUrl ?? "",
        };

        if (package.Level2 is not null)
        {
            root["Level2"] = package.Level2;
        }

        if (package.Week is not null)
        {
            root["week"] = package.Week;
        }

        if (package.VariantGroup is not null)
        {
            root["variantGroup"] = package.VariantGroup;
        }

        if (package.Variant is not null)
        {
            root["variant"] = package.Variant;
        }

        if (package.VariantSort is not null)
        {
            root["variantSort"] = package.VariantSort.Value;
        }

        var rowsArray = new JsonArray();
        foreach (var row in package.Rows)
        {
            rowsArray.Add(RowToJsonObject(row));
        }

        root["Rows"] = rowsArray;
        return root;
    }

    /// <summary>Write-side counterpart to ParseRow — see its own doc comment.</summary>
    public static JsonObject RowToJsonObject(ExmodFileRow row)
    {
        // Same rule Parse() enforces on read — validate here too, so this can never produce a row
        // its own ParseRow() would then reject on the very next read.
        EnsurePlainIdentifier(row.CurrentFile, "'CurrentFile'");

        var itemsArray = new JsonArray();
        foreach (var item in row.FileItems)
        {
            EnsurePlainIdentifier(item.Name, "item 'Name'");

            if (item.Fields.ContainsKey("Name"))
            {
                ReservedFieldNames.EnsureFieldNameAllowed(item.Name, row.CurrentFile, "Name");
            }

            var itemObj = new JsonObject { ["Name"] = item.Name };
            foreach (var (key, value) in item.Fields)
            {
                itemObj[key] = value?.DeepClone();
            }

            itemsArray.Add(itemObj);
        }

        return new JsonObject { ["CurrentFile"] = row.CurrentFile, ["File_Items"] = itemsArray };
    }

    public static string Serialize(ExmodPackage package, bool indented = true) =>
        ToJsonObject(package).ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    private static string GetRequiredString(JsonObject obj, string key) =>
        GetString(obj, key) ?? throw new FormatException($"EXMOD JSON is missing required field '{key}'.");

    /// <summary>
    /// CurrentFile/item Name aren't currently resolved to filesystem paths anywhere, so this is
    /// deliberately lighter than AssetPathGuard's path-safety rules — just enough to stop control
    /// characters or blank values from propagating into dictionary keys and warning/log messages
    /// (TableApplier, MergeRuleRegistry) built from them.
    /// </summary>
    private static void EnsurePlainIdentifier(string value, string fieldDescription)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new FormatException($"EXMOD {fieldDescription} value is invalid (blank or contains control characters): '{value}'.");
        }
    }

    private static string? GetString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new FormatException($"EXMOD JSON field '{key}' is not a string.", ex);
        }
    }

    private static int? GetInt(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new FormatException($"EXMOD JSON field '{key}' is not an integer.", ex);
        }
    }
}
