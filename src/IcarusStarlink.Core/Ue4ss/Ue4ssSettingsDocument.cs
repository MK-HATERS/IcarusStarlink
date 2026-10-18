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
    private readonly List<string> _endings;
    private readonly string _newline;
    private readonly string _descriptionSeparator;

    private Ue4ssSettingsDocument(List<string> lines, List<string> endings, string newline, string descriptionSeparator)
    {
        _lines = lines;
        _endings = endings;
        _newline = newline;
        _descriptionSeparator = descriptionSeparator;
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

    /// <param name="keepCommentLines">
    /// Keep a description's comment lines as lines (a mod's own config.ini, whose comments are often
    /// a laid-out list) instead of one run of text (UE4SS's own one-sentence comments).
    /// </param>
    public static Ue4ssSettingsDocument Parse(string text, bool keepCommentLines = false)
    {
        var (lines, endings) = TextLines.Split(text);
        return new Ue4ssSettingsDocument(lines, endings, TextLines.NewLineOf(text), keepCommentLines ? "\n" : " ");
    }

    public static Ue4ssSettingsDocument Load(string path, bool keepCommentLines = false) => Parse(File.ReadAllText(path), keepCommentLines);

    public string ToText() => TextLines.Join(_lines, _endings);

    private void AddLine(string line)
    {
        _endings[^1] = _newline;
        _lines.Add(line);
        _endings.Add("");
    }

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
            // A mod's config often documents a group of settings in one comment above them
            // ("LogReports  1 = ...", "ChatMessages  1 = ..."): the settings right under the first
            // one (no blank line between) share its comment.
            List<string> shared = [];
            var afterSetting = false;
            foreach (var line in _lines)
            {
                var trimmed = line.Trim();
                if (SectionLine().Match(line) is { Success: true } sectionMatch)
                {
                    section = sectionMatch.Groups["name"].Value.Trim();
                    comments.Clear();
                    afterSetting = false;
                }
                else if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                {
                    comments.Add(trimmed.TrimStart(';', '#').Trim());
                    afterSetting = false;
                }
                else if (trimmed.Length == 0)
                {
                    comments.Clear();
                    afterSetting = false;
                }
                else if (KeyLine().Match(line) is { Success: true } keyMatch)
                {
                    var block = comments.Count == 0 && afterSetting && _descriptionSeparator == "\n" ? shared : [.. comments];
                    settings.Add(Describe(section, keyMatch.Groups["key"].Value.Trim(), keyMatch.Groups["value"].Value.Trim(), block, _descriptionSeparator));
                    shared = block;
                    comments.Clear();
                    afterSetting = true;
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
                AddLine("");
            }

            AddLine($"[{section}]");
            AddLine($"{key} = {value}");
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

        if (insertAt == _lines.Count)
        {
            AddLine($"{key} = {value}");
        }
        else
        {
            _lines.Insert(insertAt, $"{key} = {value}");
            _endings.Insert(insertAt, _newline);
        }

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

    private static Ue4ssSetting Describe(string section, string key, string value, List<string> comments, string separator)
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

        // A mod's own list of options, one per comment line ("NoStability  buildings never become
        // unstable"): a choice when the current value is one of them.
        if (choices.Count == 0 && SimpleToken().IsMatch(value) && !IsToggle(value)
            && !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            var listed = comments.Select(c => OptionLine().Match(c)).Where(m => m.Success).Select(m => m.Groups["word"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (listed.Count > 1 && listed.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                choices.AddRange(listed);
            }
        }

        var text = string.Join(separator, description);
        var isSwitch = separator == "\n"
            ? IsModSwitch(key, value, text)
            : IsToggle(value) && (defaultValue is null || IsToggle(defaultValue)) && !NumberDescription().IsMatch(text);
        var kind = choices.Count > 1 ? Ue4ssSettingKind.Choice
            : isSwitch ? Ue4ssSettingKind.Toggle
            : Ue4ssSettingKind.Text;
        return new Ue4ssSetting(section, key, value, text, defaultValue, kind, choices);
    }

    /// <summary>
    /// A mod's own config: true/false is a switch; 0 or 1 only when the comment says what 1 means
    /// ("LogReports  1 = a report every minute") or the name reads like one (EnableX, XEnabled).
    /// Otherwise it's a number that happens to be 0 ("MinimumStability = 0", a floor).
    /// </summary>
    private static bool IsModSwitch(string key, string value, string description) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)
        || (value is "0" or "1" && (SwitchName().IsMatch(key) || OneMeans().IsMatch(description)));

    [GeneratedRegex(@"^(Enable|Disable|Use|Show|Hide|Allow|Include|Exclude|Skip|Force|Is|b[A-Z])|Enabled$|^(Debug|Verbose)$")]
    private static partial Regex SwitchName();

    [GeneratedRegex(@"(^|\s)1\s*=\s|=\s*1\s+means\b", RegexOptions.Multiline)]
    private static partial Regex OneMeans();

    [GeneratedRegex(@"^(?<word>[A-Za-z_][A-Za-z0-9_]*)\s{2,}\S")]
    private static partial Regex OptionLine();

    // A 0/1 value is a switch unless its description says it's a number ("Multiplier for Font Size").
    [GeneratedRegex(@"\b(multiplier|number of|seconds|percent|size|count|amount|scale)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberDescription();

    private static bool IsToggle(string value) => value is "0" or "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The on/off value in the same style as <paramref name="existing"/> ("1"/"0" or "true"/"false").</summary>
    public static string ToggleText(bool on, string existing) =>
        existing is "0" or "1" ? (on ? "1" : "0") : (on ? "true" : "false");

    public static bool IsOn(string value) => value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
