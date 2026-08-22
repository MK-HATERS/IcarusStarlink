using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// Real Icarus DataTable JSON (as extracted from Content\Data\data.pak by UnrealPak.exe) is
/// shaped {"RowStruct": "...", "Defaults": {...}, "Rows": [{"Name": "X", ...fields}, ...]} — an
/// array of named rows, confirmed against a real extraction. TableDiffer/TableApplier
/// (IcarusStarlink.Diffing) were designed and tested against a simpler name-keyed JsonObject
/// shape instead (row name -> {field -> value}), so this converts one into the other before
/// either of those ever sees a real game data file. "Name" becomes the object key rather than
/// staying as a field, matching the keyed shape's own convention.
/// </summary>
public static class DataTableJson
{
    /// <summary>
    /// onDuplicateRow, if given, is called with the row name whenever two Rows entries share the
    /// same "Name" — without it, the second silently overwrites the first with no diagnostic at
    /// all, which every real caller in this codebase used to accept implicitly. Not every caller
    /// has a report to feed a warning into (e.g. Weekly Changes' own tracker), so this stays opt-in
    /// rather than a required parameter.
    /// </summary>
    public static JsonObject RowsToKeyedObject(JsonObject dataTableFile, Action<string>? onDuplicateRow = null)
    {
        var result = new JsonObject();
        if (dataTableFile["Rows"] is not JsonArray rows)
        {
            return result;
        }

        foreach (var rowNode in rows)
        {
            if (rowNode is not JsonObject row
                || row["Name"] is not JsonValue nameValue
                || !nameValue.TryGetValue<string>(out var name))
            {
                // A row with no string "Name" can't be addressed by TableDiffer's keyed shape at
                // all — skip it rather than fail the whole file over one malformed entry.
                continue;
            }

            if (result.ContainsKey(name))
            {
                onDuplicateRow?.Invoke(name);
            }

            var rowCopy = row.DeepClone()!.AsObject();
            rowCopy.Remove("Name");
            result[name] = rowCopy;
        }

        return result;
    }

    /// <summary>
    /// The inverse of RowsToKeyedObject, for writing a merged table back out in Rebuild's own
    /// pipeline: rebuilds the {"RowStruct", "Defaults", "Rows": [...]} shape from a merged keyed
    /// object, reusing originalFile's own RowStruct/Defaults (a merge never touches those — only
    /// row fields). "Name" is re-inserted as each row object's first key, matching the real file's
    /// own field order, purely so a diff against the original stays readable to a human — the game
    /// itself doesn't care about JSON key order.
    /// </summary>
    public static JsonObject KeyedObjectToRows(JsonObject originalFile, JsonObject keyedTable)
    {
        var result = originalFile.DeepClone()!.AsObject();

        var rows = new JsonArray();
        foreach (var (name, rowValue) in keyedTable)
        {
            var row = new JsonObject { ["Name"] = JsonValue.Create(name) };
            if (rowValue is JsonObject rowObject)
            {
                foreach (var (fieldName, fieldValue) in rowObject)
                {
                    row[fieldName] = fieldValue?.DeepClone();
                }
            }

            rows.Add(row);
        }

        result["Rows"] = rows;
        return result;
    }
}
