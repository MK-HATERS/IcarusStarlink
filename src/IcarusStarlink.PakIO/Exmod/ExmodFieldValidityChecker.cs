using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Catches the single most insidious "why doesn't my mod actually do anything in-game" failure
/// class: a mod sets a field name that's misspelled, or that a game update has since renamed or
/// removed. The merge and the pak build both succeed with zero warnings today — TableApplier
/// happily writes whatever field name an EXMOD row specifies into the merged JSON — but Icarus's
/// own DataTable importer silently ignores a property name it doesn't recognize, so the edit has
/// no effect in-game at all. Nothing in this app's own pipeline catches that today; this runs the
/// same field-name cross-check against the real, currently-extracted game data that a human would
/// otherwise only discover by noticing their change didn't take in-game.
///
/// Deliberately narrower than staleness detection (ExmodStalenessChecker, which answers "does this
/// ITEM still exist") — this answers "is this FIELD name and rough value shape actually real,"
/// which applies just as much to an item the mod is legitimately adding as one it's editing.
/// </summary>
public static class ExmodFieldValidityChecker
{
    public sealed record InvalidField(string CurrentFile, string ItemName, string FieldName, string Reason);

    /// <param name="schemaCache">
    /// Keyed by CurrentFile, reused across a whole merge-queue pass the same way ExmodBaseDiffer's
    /// own baseTableCache is — several queued mods commonly touch the same real DataTable file, so
    /// this parses and scans each one once instead of once per mod.
    /// </param>
    public static IReadOnlyList<InvalidField> Check(
        ExmodPackage package, string dataFolder, IDictionary<string, Dictionary<string, JsonValueKind>?>? schemaCache = null)
    {
        var findings = new List<InvalidField>();

        foreach (var row in package.Rows)
        {
            // A universal terminator marker, not a game file reference — see ExmodBaseDiffer's own
            // identical exclusion for why this always fails to resolve and isn't a real problem.
            if (string.Equals(row.CurrentFile, "EndOfMod", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var schema = ResolveSchema(row.CurrentFile, dataFolder, schemaCache);
            if (schema is null)
            {
                // No matching base file at all — BaseDataFileReader's own callers already warn
                // about this elsewhere (staleness checking, Rebuild itself); nothing new to add
                // here, and every field on this row is unverifiable either way.
                continue;
            }

            foreach (var item in row.FileItems)
            {
                foreach (var (fieldName, value) in item.Fields)
                {
                    if (!schema.TryGetValue(fieldName, out var expectedKind))
                    {
                        findings.Add(new InvalidField(row.CurrentFile, item.Name, fieldName,
                            $"'{fieldName}' isn't a field {row.CurrentFile.Replace('-', '/')} actually has (typo, or renamed/removed by a game update) — this change won't do anything in-game."));
                        continue;
                    }

                    if (value?.GetValueKind() is { } actualKind && !KindsAreCompatible(expectedKind, actualKind))
                    {
                        findings.Add(new InvalidField(row.CurrentFile, item.Name, fieldName,
                            $"'{fieldName}' is normally {Describe(expectedKind)}, but this mod sets it to {Describe(actualKind)} — Icarus's own importer may reject or ignore this value."));
                    }
                }
            }
        }

        return findings;
    }

    private static Dictionary<string, JsonValueKind>? ResolveSchema(
        string currentFile, string dataFolder, IDictionary<string, Dictionary<string, JsonValueKind>?>? schemaCache)
    {
        if (schemaCache is not null && schemaCache.TryGetValue(currentFile, out var cached))
        {
            return cached;
        }

        var fileJson = BaseDataFileReader.ParseFile(dataFolder, currentFile, report: null);
        var schema = fileJson is null ? null : BuildFieldKinds(fileJson);
        schemaCache?.Add(currentFile, schema);
        return schema;
    }

    /// <summary>
    /// Real base data confirms neither Defaults nor Rows alone is a complete field list on its
    /// own — confirmed against Traits/D_Itemable.json: "Behaviour" only ever appears in Defaults,
    /// never on any actual row, while "Metadata" appears on some rows but has no entry in Defaults
    /// at all. The real, complete field list for a table is the union of both, preferring
    /// Defaults' own value (it's the struct's true declared default) when a field appears in both.
    /// </summary>
    private static Dictionary<string, JsonValueKind> BuildFieldKinds(JsonObject fileJson)
    {
        var fieldKinds = new Dictionary<string, JsonValueKind>();

        if (fileJson["Defaults"] is JsonObject defaults)
        {
            foreach (var (fieldName, value) in defaults)
            {
                if (value is not null)
                {
                    fieldKinds[fieldName] = value.GetValueKind();
                }
            }
        }

        if (fileJson["Rows"] is JsonArray rows)
        {
            foreach (var rowNode in rows)
            {
                if (rowNode is not JsonObject row)
                {
                    continue;
                }

                foreach (var (fieldName, value) in row)
                {
                    if (fieldName == "Name" || value is null || fieldKinds.ContainsKey(fieldName))
                    {
                        continue;
                    }

                    fieldKinds[fieldName] = value.GetValueKind();
                }
            }
        }

        return fieldKinds;
    }

    /// <summary>True/False are two distinct JsonValueKind values for the one conceptual boolean type — every other kind only matches itself.</summary>
    private static bool KindsAreCompatible(JsonValueKind expected, JsonValueKind actual) =>
        expected == actual || (IsBoolKind(expected) && IsBoolKind(actual));

    private static bool IsBoolKind(JsonValueKind kind) => kind is JsonValueKind.True or JsonValueKind.False;

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "true/false",
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "a nested object",
        _ => "null",
    };
}
