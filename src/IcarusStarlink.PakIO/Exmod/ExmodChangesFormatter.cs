using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Renders a mod's sparse field changes as plain, readable text — "what does this mod actually
/// change", for Library's read-only Changes tab. Not the EXMOD editor (Phase 7, still editable
/// with amber diffs); this is view-only, closer to classic IMM's own "Mod Json Viewer".
/// </summary>
public static class ExmodChangesFormatter
{
    // The default encoder escapes safe ASCII as \uXXXX (e.g. '"' -> ", '+' -> +) — a
    // sensible default for JSON embedded in HTML, actively unreadable here. Real EXMOD field
    // names use both characters constantly (the "(Value=\"Xxx_+\")" stat-name convention), so
    // this is the difference between this feature actually working and not.
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(ExmodPackage package)
    {
        if (package.Rows.Count == 0)
        {
            return "This mod doesn't change any data table fields.";
        }

        var text = new StringBuilder();
        var isFirstRow = true;

        foreach (var row in package.Rows)
        {
            if (!isFirstRow)
            {
                text.AppendLine();
            }
            isFirstRow = false;

            // EXMOD's own CurrentFile convention flattens the folder path with dashes
            // ("Traits-D_Fuel.json") — shown here as the real path ("Traits/D_Fuel.json") a
            // reader would actually recognize.
            text.AppendLine(row.CurrentFile.Replace('-', '/'));

            foreach (var item in row.FileItems)
            {
                text.AppendLine($"  {item.Name}");
                // Snapshot, not a live enumeration — same defensive .ToList() used throughout this
                // project's other item.Fields iterations, since it's a plain mutable Dictionary the
                // EXMOD editor writes into on every keystroke.
                foreach (var (fieldName, value) in item.Fields.ToList())
                {
                    text.AppendLine($"    {fieldName}: {FormatValue(value)}");
                }
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string FormatValue(JsonNode? value)
    {
        if (value is null)
        {
            return "null";
        }

        // Scalars (numbers, strings, booleans) read fine on the same line as the field name;
        // objects/arrays (e.g. a whole replaced StatsGranted) get indented underneath instead of
        // one unreadable single-line JSON blob.
        if (value is JsonValue scalar)
        {
            return scalar.ToJsonString(IndentedJson);
        }

        var indented = value.ToJsonString(IndentedJson);
        return string.Join("\n      ", indented.Split('\n'));
    }
}
