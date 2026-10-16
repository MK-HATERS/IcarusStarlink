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

    private static readonly string[] UnrealAssetExtensions = [".uasset", ".uexp", ".ubulk", ".umap"];

    /// <summary>
    /// The game files a mod replaces with its own compiled Unreal assets ("BP/Building/BP_Building_Base"
    /// for its .uasset + .uexp pair) — what a blueprint-only mod like Ferani's Steadfast Structures
    /// is made of. Readmes and pictures aren't game files.
    /// </summary>
    public static IReadOnlyList<string> ReplacedGameFiles(IEnumerable<string> assetPaths) =>
        [.. assetPaths
            .Select(p => p.Replace('\\', '/'))
            .Where(p => UnrealAssetExtensions.Any(ext => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p[..p.LastIndexOf('.')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

    /// <summary>"It works by replacing 1 game file: …" for a mod with no data rows; null when it replaces none.</summary>
    public static string? DescribeReplacedGameFiles(IReadOnlyList<string> replaced) =>
        replaced.Count == 0 ? null
            : $"It works by replacing {(replaced.Count == 1 ? "1 game file" : $"{replaced.Count} game files")} with its own: "
              + string.Join(", ", replaced.Take(4)) + (replaced.Count > 4 ? $" and {replaced.Count - 4} more" : "")
              + ". Those are compiled Unreal assets made in the Unreal editor, so Starlink can't show or change what's inside them.";

    public static string Format(ExmodPackage package, IEnumerable<string>? assetPaths = null)
    {
        // EndOfMod is a terminator every EXMOD carries, not a table — never worth a line.
        var rows = package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)).ToList();
        if (rows.Count == 0)
        {
            var replaced = DescribeReplacedGameFiles(ReplacedGameFiles(assetPaths ?? []));
            return "This mod doesn't change any data table fields." + (replaced is null ? "" : "\n\n" + replaced);
        }

        var text = new StringBuilder();
        var isFirstRow = true;

        foreach (var row in rows)
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
