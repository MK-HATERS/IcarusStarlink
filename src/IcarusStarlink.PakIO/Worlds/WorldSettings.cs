using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Worlds;

/// <summary>One of the game's custom world settings (a D_CustomGameStats row), as the world-creation screen shows it.</summary>
/// <param name="Type">"Bool", "Int" or "DropDown" (the row's StatType).</param>
/// <param name="ChangeableAnytime">StatChangeability "Anytime"; otherwise ("OnProspectCreation") only when a world is made.</param>
public sealed record WorldSettingDefinition(
    string Name, string DisplayName, string Description, string Category, string Type,
    int DefaultValue, int MinValue, int MaxValue, bool ChangeableAnytime, IReadOnlyList<string> Options);

/// <summary>
/// The game's custom world settings, read from its own D_CustomGameStats table (Stats/D_CustomGameStats.json
/// in the extracted game data): every row the game doesn't hide (SettingsVersion is hidden), with the
/// table's Defaults filling what a row leaves out.
/// </summary>
public static class WorldSettingsCatalog
{
    public static IReadOnlyList<WorldSettingDefinition> Load(string dataFolder)
    {
        var path = Path.Combine(dataFolder, "Stats", "D_CustomGameStats.json");
        if (!File.Exists(path))
        {
            return [];
        }

        var table = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        var defaults = table?["Defaults"] as JsonObject ?? [];
        var result = new List<WorldSettingDefinition>();
        foreach (var row in (table?["Rows"] as JsonArray ?? []).OfType<JsonObject>())
        {
            JsonNode? Get(string key) => row[key] ?? defaults[key];
            if (Get("bHidden")?.GetValue<bool>() == true || row["Name"]?.ToString() is not { Length: > 0 } name)
            {
                continue;
            }

            var type = Get("StatType")?.ToString() ?? "Bool";
            var options = (Get("DropDownOptions") as JsonArray ?? []).Select(o => NsLocText.TextOf(o?.ToString()) ?? o?.ToString() ?? "").ToList();
            result.Add(new WorldSettingDefinition(
                name,
                NsLocText.TextOf(Get("DisplayName")?.ToString()) ?? name,
                NsLocText.TextOf(Get("Description")?.ToString()) ?? "",
                Get("Category")?.ToString() ?? "Misc",
                type,
                Int(Get("DefaultValue")),
                Int(Get("MinIntValue")),
                type == "Bool" ? 1 : Int(Get("MaxIntValue")),
                string.Equals(Get("StatChangeability")?.ToString(), "Anytime", StringComparison.OrdinalIgnoreCase),
                options));
        }

        return result;
    }

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 0;
}

/// <summary>
/// One saved world (a prospect's .json under PlayerData\…\Prospects): what it is, and its custom
/// settings. Saving changes only the text of its ProspectInfo.CustomSettings list; every other byte
/// of the file (the world itself, its layout with tabs and CRLF as the game writes it) stays as it was.
/// </summary>
public sealed class WorldSave
{
    private WorldSave(string path, JsonObject info)
    {
        Path = path;
        Name = info["ProspectID"]?.ToString() is { Length: > 0 } id ? id : System.IO.Path.GetFileNameWithoutExtension(path);
        ProspectType = info["ProspectDTKey"]?.ToString() ?? "";
        Difficulty = info["Difficulty"]?.ToString() ?? "";
        Hardcore = info["NoRespawns"] is JsonValue h && h.TryGetValue<bool>(out var hardcore) && hardcore;
        State = info["ProspectState"]?.ToString() ?? "";
        foreach (var entry in (info["CustomSettings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (entry["SettingRowName"]?.ToString() is { Length: > 0 } setting && entry["SettingValue"] is JsonValue v && v.TryGetValue<double>(out var value))
            {
                Settings[setting] = (int)value;
            }
        }
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>The D_ProspectList row ("Outpost006_Forest").</summary>
    public string ProspectType { get; }

    public string Difficulty { get; }

    /// <summary>NoRespawns: a hardcore world.</summary>
    public bool Hardcore { get; }

    public string State { get; }

    /// <summary>The values the save holds now, by setting name (a setting it doesn't list uses the game's default).</summary>
    public Dictionary<string, int> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A world save, or null when the file isn't one (no ProspectInfo).</summary>
    public static WorldSave? Load(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["ProspectInfo"] is JsonObject info ? new WorldSave(path, info) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Every world save in a Prospects folder (newest first; the game's .backup copies aren't worlds).</summary>
    public static IReadOnlyList<WorldSave> LoadFolder(string prospectsFolder) =>
        Directory.Exists(prospectsFolder)
            ? [.. new DirectoryInfo(prospectsFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Load(f.FullName)).OfType<WorldSave>()]
            : [];

    /// <summary>
    /// Writes these setting values into the save (settings not given keep theirs; ones the save
    /// doesn't list yet are added). Copies the file into <paramref name="backupFolder"/> first and
    /// writes through a temp file, so a failed write never leaves half a world.
    /// Returns the backup's path.
    /// </summary>
    public string Save(IReadOnlyDictionary<string, int> values, string backupFolder)
    {
        var text = File.ReadAllText(Path);
        var merged = new List<KeyValuePair<string, int>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Settings)
        {
            merged.Add(new(name, values.TryGetValue(name, out var changed) ? changed : value));
            seen.Add(name);
        }

        merged.AddRange(values.Where(kv => !seen.Contains(kv.Key)));
        var updated = ReplaceCustomSettings(text, merged);

        Directory.CreateDirectory(backupFolder);
        var backup = System.IO.Path.Combine(backupFolder, $"{DateTime.Now:yyyy-MM-dd_HHmmss_fff}_{System.IO.Path.GetFileName(Path)}");
        File.Copy(Path, backup, overwrite: true);
        var temp = Path + ".isl-tmp";
        File.WriteAllText(temp, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, Path, overwrite: true);

        foreach (var (name, value) in merged)
        {
            Settings[name] = value;
        }

        return backup;
    }

    /// <summary>
    /// The file's text with ProspectInfo's CustomSettings list replaced (or added after its last
    /// field when the save predates custom settings), written the way the game writes it.
    /// </summary>
    internal static string ReplaceCustomSettings(string text, IReadOnlyList<KeyValuePair<string, int>> settings)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var infoStart = FindKey(text, "ProspectInfo", 0) ?? throw new FormatException("Not a world save: no ProspectInfo.");
        var infoOpen = text.IndexOf('{', infoStart);
        var infoClose = MatchingClose(text, infoOpen);
        var indent = LineIndent(text, infoStart) + "\t";

        var list = new StringBuilder("[");
        for (var i = 0; i < settings.Count; i++)
        {
            list.Append(newline).Append(indent).Append('\t').Append('{')
                .Append(newline).Append(indent).Append("\t\t\"SettingRowName\": ").Append(JsonSerializer.Serialize(settings[i].Key)).Append(',')
                .Append(newline).Append(indent).Append("\t\t\"SettingValue\": ").Append(settings[i].Value)
                .Append(newline).Append(indent).Append('\t').Append('}').Append(i < settings.Count - 1 ? "," : "");
        }

        list.Append(newline).Append(indent).Append(']');

        if (FindKey(text, "CustomSettings", infoOpen) is { } key && key < infoClose)
        {
            var open = text.IndexOf('[', key);
            var close = MatchingClose(text, open);
            return string.Concat(text.AsSpan(0, open), list.ToString(), text.AsSpan(close + 1));
        }

        // Older save without the list: add it as ProspectInfo's last field.
        var lastValueEnd = infoClose - 1;
        while (lastValueEnd > infoOpen && char.IsWhiteSpace(text[lastValueEnd]))
        {
            lastValueEnd--;
        }

        return string.Concat(text.AsSpan(0, lastValueEnd + 1), "," + newline + indent + "\"CustomSettings\": " + list, text.AsSpan(lastValueEnd + 1));
    }

    /// <summary>Where "key": starts (outside strings), searching from <paramref name="from"/>.</summary>
    private static int? FindKey(string text, string key, int from)
    {
        var needle = "\"" + key + "\"";
        var inString = false;
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                if (string.CompareOrdinal(text, i, needle, 0, needle.Length) == 0)
                {
                    var after = i + needle.Length;
                    while (after < text.Length && char.IsWhiteSpace(text[after]))
                    {
                        after++;
                    }

                    if (after < text.Length && text[after] == ':')
                    {
                        return i;
                    }
                }

                inString = true;
            }
        }

        return null;
    }

    /// <summary>The index of the bracket closing the one at <paramref name="open"/> (strings skipped).</summary>
    private static int MatchingClose(string text, int open)
    {
        if (open < 0)
        {
            throw new FormatException("Not a world save: a list or object isn't closed.");
        }

        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    if (--depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        throw new FormatException("Not a world save: a list or object isn't closed.");
    }

    private static string LineIndent(string text, int at)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        var end = lineStart;
        while (end < text.Length && text[end] is ' ' or '\t')
        {
            end++;
        }

        return text[lineStart..end];
    }
}
