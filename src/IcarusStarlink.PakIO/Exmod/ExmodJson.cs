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
    /// <summary>How the editor shows JSON: indented, and accents and symbols left readable instead of escaped.</summary>
    public static readonly JsonSerializerOptions DisplayOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ExmodPackage Parse(string json)
    {
        JsonNode? parsed;
        try
        {
            // Duplicate-tolerant rather than JsonNode.Parse: real mods in the wild repeat a key
            // inside one object, and rejecting those made a working mod completely unusable here
            // (see DuplicateTolerantJson). Last occurrence wins, matching normal JSON semantics.
            parsed = DuplicateTolerantJson.Parse(json);
        }
        catch (JsonException ex)
        {
            // Malformed JSON syntax (truncated content, a stray comma) is exactly the same
            // "unreadable EXMOD" case every other failure mode in this class already surfaces as
            // FormatException — every caller (FolderLibraryRepository.RescanAll's skip-and-log
            // path in particular) is written against that one documented exception type, not a
            // raw framework exception this specific failure mode happened to throw.
            throw new FormatException("EXMOD JSON is not valid JSON.", ex);
        }

        var root = parsed as JsonObject
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
        // Real files in the wild don't all agree on this shape — a classic-IMM-produced merged
        // pack's own EXMOD omits "description" entirely, and at least one real community mod uses
        // an older "ModName" field instead of "name"/"fileName" altogether (no author/version/
        // description at all). Treating name/fileName/author/version/description as required had
        // this app silently refuse to read real, legitimate mods rather than degrading gracefully
        // — Rows/File_Items (the actual mod content) is always present and always required; the
        // header metadata around it is now best-effort, matching how an opaque pak's own
        // synthesized header (ToOpaquePakEntry) already treats a missing name/author as "Unknown"
        // rather than a hard failure.
        var legacyModName = GetString(root, "ModName");
        var name = GetString(root, "name") ?? legacyModName ?? throw new FormatException("EXMOD JSON is missing required field 'name' (and has no 'ModName' fallback either).");
        var fileName = GetString(root, "fileName") ?? AssetPathGuard.SanitizeToSimpleFileName(legacyModName ?? name);
        AssetPathGuard.EnsureSimpleFileName(fileName);

        var package = new ExmodPackage
        {
            Name = name,
            Author = GetString(root, "author") ?? "Unknown",
            Version = GetString(root, "version") ?? "",
            Description = GetString(root, "description") ?? "",
            FileName = fileName,
            ImageUrl = GetString(root, "imageURL"),
            ReadmeUrl = GetString(root, "readmeURL"),
            Level2 = GetString(root, "Level2"),
            Week = GetString(root, "week"),
            VariantGroup = GetString(root, "variantGroup"),
            Variant = GetString(root, "variant"),
            VariantSort = GetInt(root, "variantSort"),
        };

        package.ScaleRules = ParseScaleRules(root[ScaleRulesKey]);

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
    /// One Rows entry: {"CurrentFile": ..., "File_Items": [...]}, parsed with the same validation
    /// ParseCore applies to every row (ParseCore's own loop calls it).
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
                row.FileItems.Add(ExmodFileItem.FromRow(itemName, itemObj));
            }
        }

        return row;
    }

    private const string ScaleRulesKey = "starlinkScaleRules";

    /// <summary>Lenient on purpose: a rule this version can't read is skipped, never a reason to refuse the whole mod.</summary>
    private static List<ScaleRule> ParseScaleRules(JsonNode? node)
    {
        var rules = new List<ScaleRule>();
        if (node is not JsonArray array)
        {
            return rules;
        }

        foreach (var entry in array.OfType<JsonObject>())
        {
            try
            {
                var rule = new ScaleRule
                {
                    Enabled = entry["enabled"]?.GetValue<bool>() ?? true,
                    Label = entry["label"]?.GetValue<string>() ?? "",
                    CurrentFile = entry["currentFile"]?.GetValue<string>() ?? "",
                    Field = entry["field"]?.GetValue<string>() ?? "",
                    Operation = Enum.TryParse<ScaleOperation>(entry["operation"]?.GetValue<string>(), ignoreCase: true, out var op) && Enum.IsDefined(op) ? op : ScaleOperation.Multiply,
                    Value = entry["value"]?.GetValue<double>() ?? 1,
                    RowFilter = entry["rowFilter"]?.GetValue<string>(),
                    IncludeInherited = entry["includeInherited"]?.GetValue<bool>() ?? true,
                    // Added later: older files have no list (the rule excepts no row). Anything but a name is skipped.
                    ExceptRows = entry["exceptRows"] is JsonArray except
                        ? [.. except.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s.Trim() : null).OfType<string>().Where(s => s.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)]
                        : [],
                };
                if (rule.CurrentFile.Length > 0 && rule.Field.Length > 0)
                {
                    rules.Add(rule);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                // Wrong value type in a hand-edited rule — skip that one rule.
            }
        }

        return rules;
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

        if (package.ScaleRules.Count > 0)
        {
            root[ScaleRulesKey] = new JsonArray([.. package.ScaleRules.Select(r =>
            {
                var rule = new JsonObject
                {
                    ["enabled"] = r.Enabled,
                    ["label"] = r.Label,
                    ["currentFile"] = r.CurrentFile,
                    ["field"] = r.Field,
                    ["operation"] = r.Operation.ToString(),
                    ["value"] = r.Value,
                    ["rowFilter"] = r.RowFilter,
                    ["includeInherited"] = r.IncludeInherited,
                };

                // Only when there are any, so a rule without exceptions is written exactly as before.
                if (r.ExceptRows.Count > 0)
                {
                    rule["exceptRows"] = new JsonArray([.. r.ExceptRows.Select(n => (JsonNode)JsonValue.Create(n)!)]);
                }

                return (JsonNode)rule;
            })]);
        }

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
            // A snapshot, not a live enumeration — the EXMOD editor writes each keystroke straight
            // into this same Dictionary on the UI thread (EditorFieldViewModel's own established
            // design), and a Save triggered while a field edit's own binding update is still
            // in-flight could otherwise mutate the dictionary mid-enumeration here, corrupting its
            // internal state (a real, reproduced crash: "Save failed: Operations that change
            // non-concurrent collections must have exclusive access..."). Matches the same
            // defensive .ToList() PopulateFieldsForSelection already uses for exactly this reason.
            foreach (var (key, value) in item.Fields.ToList())
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

        // A scalar of the wrong JSON type is coerced to its text, not refused: tools disagree on
        // these header fields (a numeric "version": 1.2, a boolean "Level2": true where classic IMM
        // writes the string "True"), and refusing the whole mod over one of them contradicted the
        // best-effort-header rule in ParseCore. Objects/arrays still fail below — those are
        // structural, not a formatting quirk.
        if (node is JsonValue scalar && !scalar.TryGetValue<string>(out _))
        {
            return scalar.ToJsonString();
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

        // Same leniency as GetString: a numeric string ("variantSort": "1") is accepted as the number.
        if (node is JsonValue text && text.TryGetValue<string>(out var asText) && int.TryParse(asText, out var parsed))
        {
            return parsed;
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
