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
    /// <param name="IsUnknownField">The table has no such field (otherwise: a value of another kind than the game's).</param>
    /// <param name="SuggestedField">The table's field the mod most likely means: the same name in other case, or the one close name.</param>
    public sealed record InvalidField(string CurrentFile, string ItemName, string FieldName, string Reason, bool IsUnknownField = false, string? SuggestedField = null)
    {
        /// <summary>Only the case differs from the game's field: the game most likely reads it anyway, and matching the spelling makes sure.</summary>
        public bool IsCaseOnly => SuggestedField is not null && string.Equals(SuggestedField, FieldName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How far a typo may be from the real field name (edits) and still be offered.</summary>
    private const int MaxSuggestionDistance = 2;

    /// <param name="schemaCache">
    /// Keyed by CurrentFile, reused across a whole merge-queue pass the same way ExmodBaseDiffer's
    /// own baseTableCache is — several queued mods commonly touch the same real DataTable file, so
    /// this parses and scans each one once instead of once per mod.
    /// </param>
    public static IReadOnlyList<InvalidField> Check(
        ExmodPackage package, string dataFolder, IDictionary<string, Dictionary<string, JsonValueKind>?>? schemaCache = null) =>
        Check(package, currentFile => ResolveSchema(currentFile, dataFolder, schemaCache));

    /// <summary>The same check from tables the health check has already read (GameTableIndex), each table's field list built once per call.</summary>
    public static IReadOnlyList<InvalidField> Check(ExmodPackage package, GameTableIndex gameIndex)
    {
        var schemas = new Dictionary<string, Dictionary<string, JsonValueKind>?>(StringComparer.OrdinalIgnoreCase);
        return Check(package, currentFile =>
        {
            if (!schemas.TryGetValue(currentFile, out var schema))
            {
                schema = gameIndex.Rows(currentFile) is { } rows
                    ? BuildFieldKinds(gameIndex.Defaults(currentFile), rows.Select(r => r.Value).OfType<JsonObject>())
                    : null;
                schemas[currentFile] = schema;
            }

            return schema;
        });
    }

    private static IReadOnlyList<InvalidField> Check(ExmodPackage package, Func<string, Dictionary<string, JsonValueKind>?> schemaFor)
    {
        var findings = new List<InvalidField>();

        foreach (var row in package.Rows)
        {
            // See ExmodSentinelFiles.IsEndOfModMarker's own doc comment.
            if (ExmodSentinelFiles.IsEndOfModMarker(row.CurrentFile))
            {
                continue;
            }

            var schema = schemaFor(row.CurrentFile);
            if (schema is null)
            {
                // No matching base file at all — BaseDataFileReader's own callers already warn
                // about this elsewhere (staleness checking, Rebuild itself); nothing new to add
                // here, and every field on this row is unverifiable either way.
                continue;
            }

            // A row listed twice: the last one is what the merge uses (as the health check judges it),
            // and one finding per place, not one per copy.
            foreach (var item in row.FileItems.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            {
                foreach (var (fieldName, value) in item.Fields)
                {
                    if (!schema.TryGetValue(fieldName, out var expectedKind))
                    {
                        // A null deletes the field: deleting one the table doesn't have changes
                        // nothing (the health check lists that as tidying), and renaming it to the
                        // game's spelling would delete the game's field instead.
                        if (value is null)
                        {
                            continue;
                        }

                        var suggestion = Suggest(fieldName, schema.Keys);
                        // The rename would clash with the field the row already has under that name.
                        if (suggestion is not null && item.Fields.ContainsKey(suggestion))
                        {
                            suggestion = null;
                        }

                        var table = GameTableIndex.TableNameOf(row.CurrentFile);
                        var reason = suggestion is not null && string.Equals(suggestion, fieldName, StringComparison.OrdinalIgnoreCase)
                            ? $"Spelled '{fieldName}' here; {table} spells it '{suggestion}'. The game most likely reads it either way; matching the spelling makes sure."
                            : suggestion is not null
                                ? $"{table} has no field '{fieldName}', so this change does nothing in game. It most likely means '{suggestion}'."
                                : $"{table} has no field '{fieldName}' (a typo, or a game update renamed or removed it), so this change does nothing in game.";
                        findings.Add(new InvalidField(row.CurrentFile, item.Name, fieldName, reason, IsUnknownField: true, suggestion));
                        continue;
                    }

                    if (value?.GetValueKind() is { } actualKind && !KindsAreCompatible(expectedKind, actualKind))
                    {
                        findings.Add(new InvalidField(row.CurrentFile, item.Name, fieldName,
                            $"The game has {Describe(expectedKind)} here, and this mod sets {Describe(actualKind)}, so the game may ignore it."));
                    }
                }
            }
        }

        return findings;
    }

    /// <summary>The same name in other case, else the one field within a couple of typing mistakes (none when two are as close).</summary>
    private static string? Suggest(string fieldName, IEnumerable<string> fields)
    {
        var names = fields.ToList();
        if (names.FirstOrDefault(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase)) is { } sameName)
        {
            return sameName;
        }

        // Lengths further apart than the distance can't be that close (and a huge name from a mod file stays cheap).
        var close = names.Where(f => Math.Abs(f.Length - fieldName.Length) <= MaxSuggestionDistance).Select(f => (Field: f, Distance: LevenshteinDistance.Compute(f.ToLowerInvariant(), fieldName.ToLowerInvariant())))
            .Where(c => c.Distance <= MaxSuggestionDistance)
            .OrderBy(c => c.Distance)
            .ToList();
        return close.Count == 1 || (close.Count > 1 && close[0].Distance < close[1].Distance) ? close[0].Field : null;
    }

    private static Dictionary<string, JsonValueKind>? ResolveSchema(
        string currentFile, string dataFolder, IDictionary<string, Dictionary<string, JsonValueKind>?>? schemaCache)
    {
        if (schemaCache is not null && schemaCache.TryGetValue(currentFile, out var cached))
        {
            return cached;
        }

        var fileJson = BaseDataFileReader.ParseFile(dataFolder, currentFile, report: null);
        var schema = fileJson is null ? null : BuildFieldKinds(fileJson["Defaults"] as JsonObject, (fileJson["Rows"] as JsonArray ?? []).OfType<JsonObject>());
        schemaCache?.Add(currentFile, schema);
        return schema;
    }

    /// <summary>
    /// Real base data confirms neither Defaults nor Rows alone is a complete field list on its
    /// own — confirmed against Traits/D_Itemable.json: "Behaviour" only ever appears in Defaults,
    /// never on any actual row, while "Metadata" appears on some rows but has no entry in Defaults
    /// at all. The real, complete field list for a table is the union of both, preferring
    /// Defaults' own value (it's the struct's true declared default) when a field appears in both.
    ///
    /// "Metadata" is in no table's Defaults at all, and on rows of 76 of the 299 tables (checked
    /// 2026-10-25): every row has it (DLC gating is Metadata.RequiredFeatureLevel), only left out
    /// of the extraction when empty, so it counts as a field of every table.
    /// </summary>
    private static Dictionary<string, JsonValueKind> BuildFieldKinds(JsonObject? defaults, IEnumerable<JsonObject> rows)
    {
        var fieldKinds = new Dictionary<string, JsonValueKind>();

        foreach (var (fieldName, value) in defaults ?? [])
        {
            if (value is not null)
            {
                fieldKinds[fieldName] = value.GetValueKind();
            }
        }

        foreach (var row in rows)
        {
            foreach (var (fieldName, value) in row)
            {
                if (fieldName == "Name" || value is null || fieldKinds.ContainsKey(fieldName))
                {
                    continue;
                }

                fieldKinds[fieldName] = value.GetValueKind();
            }
        }

        fieldKinds.TryAdd("Metadata", JsonValueKind.Object);
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
