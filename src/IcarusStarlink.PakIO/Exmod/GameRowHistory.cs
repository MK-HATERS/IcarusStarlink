using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.Core.IO;

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
    private readonly List<string> _dates = [];
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

        var found = new List<(string CurrentFile, List<string> Names)>();
        foreach (var path in Directory.EnumerateFiles(dataFolder, "D_*.json", SearchOption.AllDirectories))
        {
            var currentFile = Path.GetRelativePath(dataFolder, path).Replace(Path.DirectorySeparatorChar, '-').Replace('/', '-');
            if (RowNames(path) is { } names)
            {
                found.Add((currentFile, names));
            }
        }

        // A refresh that changed no row name (the same game version extracted again) adds nothing:
        // a second identical version would make a removal look one update later than it was.
        var fingerprint = Fingerprint(found);
        if (versions.Count > 0 && versions[^1] is JsonObject last && last["rows"]?.ToString() == fingerprint)
        {
            return;
        }

        var index = versions.Count;
        versions.Add(new JsonObject
        {
            ["label"] = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["date"] = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["rows"] = fingerprint,
        });

        foreach (var (currentFile, names) in found)
        {
            if (tables[currentFile] is not JsonObject rows)
            {
                tables[currentFile] = rows = [];
            }

            foreach (var name in names)
            {
                var first = rows[name] is JsonArray { Count: 2 } seen && seen[0] is JsonValue firstValue && firstValue.TryGetValue<int>(out var firstAt) && firstAt >= 0 && firstAt < index
                    ? firstAt
                    : index;
                rows[name] = new JsonArray(first, index);
            }
        }

        root["versions"] = versions;
        root["tables"] = tables;
        AtomicFile.WriteAllText(localFile, root.ToJsonString(), new UTF8Encoding(false));
    }

    /// <summary>The row names of one game version, as a short hash (to spot a refresh that changed nothing).</summary>
    private static string Fingerprint(List<(string CurrentFile, List<string> Names)> tables)
    {
        var text = new StringBuilder();
        foreach (var (file, names) in tables.OrderBy(t => t.CurrentFile, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(file.ToLowerInvariant()).Append('\n');
            foreach (var name in names.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal))
            {
                text.Append(name).Append('\n');
            }
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>
    /// Adds a history file's versions after the ones already here (shipped first, then the user's
    /// own). A version dated on or before the newest one already here is left out: the user's part
    /// recorded before this release's shipped history was built would otherwise land after it,
    /// out of order, and make rows the game removed since look as if it still had them.
    /// </summary>
    private void Merge(JsonObject? root)
    {
        if (root?["versions"] is not JsonArray versions || root["tables"] is not JsonObject tables)
        {
            return;
        }

        // Where each of this file's versions lands here (-1: left out). Only compared with the parts
        // merged before: the shipped history itself has several versions on one date.
        var newestDate = _dates.Count > 0 ? _dates[^1] : null;
        var position = new int[versions.Count];
        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i] as JsonObject;
            var date = version?["date"]?.ToString();
            if (version is null || (newestDate is not null && date is not null && string.CompareOrdinal(date, newestDate) <= 0))
            {
                position[i] = -1;
                continue;
            }

            position[i] = _versions.Count;
            _versions.Add(Label(version["label"]?.ToString(), date));
            _dates.Add(date ?? newestDate ?? "");
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
                // Anything but two whole numbers is a damaged entry (Load never throws): left out.
                if (seenNode is not JsonArray { Count: 2 } seen
                    || seen[0] is not JsonValue firstValue || !firstValue.TryGetValue<int>(out var firstAt)
                    || seen[1] is not JsonValue lastValue || !lastValue.TryGetValue<int>(out var lastAt))
                {
                    continue;
                }

                // Seen only in versions left out: the history already here covers that time.
                if (firstAt < 0 || lastAt >= position.Length || firstAt > lastAt || position[lastAt] < 0)
                {
                    continue;
                }

                var first = Enumerable.Range(firstAt, lastAt - firstAt + 1).Select(i => position[i]).First(p => p >= 0);
                var last = position[lastAt];
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
