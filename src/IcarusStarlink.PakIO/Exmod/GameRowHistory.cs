using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// When the game had a row: the first and last game version it was seen in ("Week 142", "4 Sep 2026"),
/// and their positions in the history (0 = oldest) for comparing sightings.
/// </summary>
public sealed record GameRowSighting(string FirstSeen, string LastSeen, int First, int Last);

/// <summary>
/// Every data-table row name the game has ever had, with the first and last version it was in, so
/// a row the game doesn't have today can be told apart: one it had and removed (an old copy a mod
/// still carries) versus one it never had (the mod's own new content).
///
/// Two parts. The app ships the history of every public game version since Week 9, built from the
/// git histories of the extracted game data (tools/GameRowHistory), so it works on a fresh install.
/// Each Refresh game data then adds that game version to a small file of the user's own
/// (<see cref="Record"/>), so rows the game removes after this release are known too.
/// Only row names and version labels are kept, no game content.
/// </summary>
public sealed class GameRowHistory
{
    private const string ResourceName = "game_row_history.json.gz";

    public static GameRowHistory Empty { get; } = new([], new(StringComparer.OrdinalIgnoreCase));

    private readonly List<string> _versions;
    private readonly Dictionary<string, Dictionary<string, (int First, int Last)>> _tables;

    private GameRowHistory(List<string> versions, Dictionary<string, Dictionary<string, (int First, int Last)>> tables)
    {
        _versions = versions;
        _tables = tables;
    }

    public int VersionCount => _versions.Count;

    /// <summary>The newest version known (the user's last refresh, or the shipped history's newest).</summary>
    public int Latest => _versions.Count - 1;

    /// <summary>A version's label ("Week 251", "16 Oct 2026").</summary>
    public string LabelOf(int version) => _versions[Math.Clamp(version, 0, Math.Max(0, _versions.Count - 1))];

    /// <summary>The user's own part, next to the app's Data folder (in its Cache).</summary>
    public static string LocalFileFor(string dataFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar)) ?? dataFolder, "Cache", "game_row_history_local.json");

    /// <summary>The shipped history plus the user's own (when <paramref name="localFile"/> exists). Never throws: a damaged part is left out.</summary>
    public static GameRowHistory Load(string? localFile)
    {
        var history = new GameRowHistory([], new(StringComparer.OrdinalIgnoreCase));
        try
        {
            using var stream = typeof(GameRowHistory).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                history.Merge(JsonNode.Parse(gzip) as JsonObject);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            // Without the shipped part, only the user's own refreshes are known.
        }

        if (localFile is not null && File.Exists(localFile))
        {
            try
            {
                history.Merge(JsonNode.Parse(File.ReadAllText(localFile)) as JsonObject);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A damaged local file is rebuilt by the next Record.
            }
        }

        return history;
    }

    /// <summary>When the game had this row, or null when no known game version had it.</summary>
    public GameRowSighting? Find(string currentFile, string rowName) =>
        _tables.TryGetValue(currentFile, out var rows) && rows.TryGetValue(rowName, out var seen)
            ? new GameRowSighting(_versions[seen.First], _versions[seen.Last], seen.First, seen.Last)
            : null;

    /// <summary>
    /// Adds the game version now in <paramref name="dataFolder"/> (just refreshed) to the user's own
    /// part. Reads every table's row names; written through a temp file.
    /// </summary>
    public static void Record(string dataFolder, string localFile, DateTimeOffset when)
    {
        if (!Directory.Exists(dataFolder))
        {
            return;
        }

        JsonObject root;
        try
        {
            root = File.Exists(localFile) ? JsonNode.Parse(File.ReadAllText(localFile)) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            root = [];
        }

        var versions = root["versions"] as JsonArray ?? [];
        var tables = root["tables"] as JsonObject ?? [];
        var index = versions.Count;
        versions.Add(new JsonObject { ["label"] = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["date"] = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });

        foreach (var path in Directory.EnumerateFiles(dataFolder, "D_*.json", SearchOption.AllDirectories))
        {
            var currentFile = Path.GetRelativePath(dataFolder, path).Replace(Path.DirectorySeparatorChar, '-').Replace('/', '-');
            if (RowNames(path) is not { } names)
            {
                continue;
            }

            if (tables[currentFile] is not JsonObject rows)
            {
                tables[currentFile] = rows = [];
            }

            foreach (var name in names)
            {
                var first = rows[name] is JsonArray seen && seen.Count == 2 ? seen[0]!.GetValue<int>() : index;
                rows[name] = new JsonArray(first, index);
            }
        }

        root["versions"] = versions;
        root["tables"] = tables;
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        var temp = localFile + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(), new UTF8Encoding(false));
        File.Move(temp, localFile, overwrite: true);
    }

    /// <summary>Adds a history file's versions after the ones already here (shipped first, then the user's own).</summary>
    private void Merge(JsonObject? root)
    {
        if (root?["versions"] is not JsonArray versions || root["tables"] is not JsonObject tables)
        {
            return;
        }

        var offset = _versions.Count;
        foreach (var version in versions.OfType<JsonObject>())
        {
            _versions.Add(Label(version["label"]?.ToString(), version["date"]?.ToString()));
        }

        foreach (var (file, rowsNode) in tables)
        {
            if (rowsNode is not JsonObject rows)
            {
                continue;
            }

            if (!_tables.TryGetValue(file, out var known))
            {
                _tables[file] = known = new(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var (row, seenNode) in rows)
            {
                if (seenNode is not JsonArray { Count: 2 } seen)
                {
                    continue;
                }

                var (first, last) = (seen[0]!.GetValue<int>() + offset, seen[1]!.GetValue<int>() + offset);
                known[row] = known.TryGetValue(row, out var before) ? (Math.Min(before.First, first), Math.Max(before.Last, last)) : (first, last);
            }
        }
    }

    /// <summary>"Week 142" as is; a date as "4 Sep 2026".</summary>
    private static string Label(string? label, string? date) =>
        label is { Length: > 0 } && label.StartsWith("Week", StringComparison.OrdinalIgnoreCase)
            ? label
            : DateTime.TryParseExact(date ?? label, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : label ?? date ?? "an earlier version";

    private static List<string>? RowNames(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("Rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var names = new List<string>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } text)
                {
                    names.Add(text);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
