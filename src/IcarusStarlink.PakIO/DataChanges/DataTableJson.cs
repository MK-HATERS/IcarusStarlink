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
    public static JsonObject RowsToKeyedObject(JsonObject dataTableFile)
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

            var rowCopy = row.DeepClone()!.AsObject();
            rowCopy.Remove("Name");
            result[name] = rowCopy;
        }

        return result;
    }
}
