using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>What kind of control a UE4SS setting needs.</summary>
public enum Ue4ssSettingKind
{
    Text,

    /// <summary>On/off, written the way the file already writes it ("1"/"0" or "true"/"false").</summary>
    Toggle,

    /// <summary>One of a fixed list ("Valid values (case-insensitive): EngineTick, ProcessEvent").</summary>
    Choice,
}

/// <summary>One "Key = Value" line of UE4SS-settings.ini, with what its comment block says about it.</summary>
public sealed record Ue4ssSetting(
    string Section, string Key, string Value, string Description, string? Default, Ue4ssSettingKind Kind, IReadOnlyList<string> Choices);

/// <summary>
/// UE4SS-settings.ini, read and written line by line so that saving only changes the value part of
/// the lines that were edited: every comment, blank line, ordering and the file's own newline style
/// survive. Each setting's description is the comment block right above it, the way UE4SS documents
/// its own settings ("; Default: 1", "; Valid values (case-insensitive): A, B").
/// </summary>
public sealed partial class Ue4ssSettingsDocument
{
    private readonly List<string> _lines;
    private readonly string _newline;

    private Ue4ssSettingsDocument(List<string> lines, string newline)
    {
        _lines = lines;
        _newline = newline;
    }

    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]\s*$")]
    private static partial Regex SectionLine();

    [GeneratedRegex(@"^(?<lead>\s*)(?<key>[^;#=\[\s][^=]*?)(?<eq>\s*=[ \t]?)(?<value>.*)$")]
    private static partial Regex KeyLine();

    [GeneratedRegex(@"^Default:\s*(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultComment();

    [GeneratedRegex(@"^Valid values[^:]*:\s*(?<values>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ValidValuesComment();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex SimpleToken();

    public static Ue4ssSettingsDocument Parse(string text)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        return new Ue4ssSettingsDocument(lines, newline);
    }

    public static Ue4ssSettingsDocument Load(string path) => Parse(File.ReadAllText(path));

    public string ToText() => string.Join(_newline, _lines);

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToText());
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Every setting in file order, with its section and what its comment block says.</summary>
    public IReadOnlyList<Ue4ssSetting> Settings
    {
        get
        {
            var settings = new List<Ue4ssSetting>();
            var section = "";
            var comments = new List<string>();
            foreach (var line in _lines)
            {
                var trimmed = line.Trim();
                if (SectionLine().Match(line) is { Success: true } sectionMatch)
                {
                    section = sectionMatch.Groups["name"].Value.Trim();
                    comments.Clear();
                }
                else if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                {
                    comments.Add(trimmed.TrimStart(';', '#').Trim());
                }
                else if (trimmed.Length == 0)
                {
                    comments.Clear();
                }
                else if (KeyLine().Match(line) is { Success: true } keyMatch)
                {
                    settings.Add(Describe(section, keyMatch.Groups["key"].Value.Trim(), keyMatch.Groups["value"].Value.Trim(), comments));
                    comments.Clear();
                }
            }

            return settings;
        }
    }

    public string? GetValue(string section, string key) =>
        FindLine(section, key) is { } index ? KeyLine().Match(_lines[index]).Groups["value"].Value.Trim() : null;

    /// <summary>Changes one value in place (adding the key to its section, or the section, when the file doesn't have it yet). True when the file changed.</summary>
    public bool SetValue(string section, string key, string value)
    {
        value = value.Trim();
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("A UE4SS setting can't span lines.", nameof(value));
        }

        if (FindLine(section, key) is { } index)
        {
            var match = KeyLine().Match(_lines[index]);
            if (match.Groups["value"].Value.Trim() == value)
            {
                return false;
            }

            var eq = match.Groups["eq"].Value;
            _lines[index] = $"{match.Groups["lead"].Value}{match.Groups["key"].Value}{(eq.EndsWith(' ') || value.Length == 0 ? eq : eq + " ")}{value}";
            return true;
        }

        var sectionStart = FindSection(section);
        if (sectionStart is null)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0)
            {
                _lines.Add("");
            }

            _lines.Add($"[{section}]");
            _lines.Add($"{key} = {value}");
            return true;
        }

        // After the section's last setting, so it sits with its neighbours.
        var insertAt = sectionStart.Value + 1;
        for (var i = sectionStart.Value + 1; i < _lines.Count && !SectionLine().IsMatch(_lines[i]); i++)
        {
            if (KeyLine().IsMatch(_lines[i]) && !_lines[i].TrimStart().StartsWith(';'))
            {
                insertAt = i + 1;
            }
        }

        _lines.Insert(insertAt, $"{key} = {value}");
        return true;
    }

    private int? FindSection(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (SectionLine().Match(_lines[i]) is { Success: true } m && m.Groups["name"].Value.Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    private int? FindLine(string section, string key)
    {
        var current = "";
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (SectionLine().Match(line) is { Success: true } sectionMatch)
            {
                current = sectionMatch.Groups["name"].Value.Trim();
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (current.Equals(section, StringComparison.OrdinalIgnoreCase)
                && KeyLine().Match(line) is { Success: true } keyMatch
                && keyMatch.Groups["key"].Value.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    private static Ue4ssSetting Describe(string section, string key, string value, List<string> comments)
    {
        string? defaultValue = null;
        var choices = new List<string>();
        var description = new List<string>();
        foreach (var comment in comments)
        {
            if (DefaultComment().Match(comment) is { Success: true } d)
            {
                defaultValue = d.Groups["value"].Value.Trim();
            }
            else if (ValidValuesComment().Match(comment) is { Success: true } v)
            {
                var tokens = v.Groups["values"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 1 && tokens.All(t => SimpleToken().IsMatch(t)))
                {
                    choices.AddRange(tokens);
                }
                else
                {
                    description.Add(comment); // "Anything from Mods/Keybinds/..." is advice, not a list
                }
            }
            else
            {
                description.Add(comment);
            }
        }

        var text = string.Join(" ", description);
        var kind = choices.Count > 1 ? Ue4ssSettingKind.Choice
            : IsToggle(value) && (defaultValue is null || IsToggle(defaultValue)) && !NumberDescription().IsMatch(text) ? Ue4ssSettingKind.Toggle
            : Ue4ssSettingKind.Text;
        return new Ue4ssSetting(section, key, value, text, defaultValue, kind, choices);
    }

    // A 0/1 value is a switch unless its description says it's a number ("Multiplier for Font Size").
    [GeneratedRegex(@"\b(multiplier|number of|seconds|percent|size|count|amount|scale)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberDescription();

    private static bool IsToggle(string value) => value is "0" or "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The on/off value in the same style as <paramref name="existing"/> ("1"/"0" or "true"/"false").</summary>
    public static string ToggleText(bool on, string existing) =>
        existing is "0" or "1" ? (on ? "1" : "0") : (on ? "true" : "false");

    public static bool IsOn(string value) => value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
