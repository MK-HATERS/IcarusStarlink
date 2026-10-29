using System.Text.RegularExpressions;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>What kind of control a UE4SS setting needs.</summary>
public enum Ue4ssSettingKind
{
    Text,

    /// <summary>On/off, written the way the file already writes it ("1"/"0" or "true"/"false").</summary>
    Toggle,

    /// <summary>One of a fixed list ("Valid values (case-insensitive): EngineTick, ProcessEvent").</summary>
    Choice,

    /// <summary>A number (a mod's own config): a box, or a slider when the mod asks for one ("[slider 1 10 0.5]").</summary>
    Number,

    /// <summary>A key (a mod's Lua config): "R" with a [key] tag, or a one-line { key = "F4", ctrl = true } table.</summary>
    Key,
}

/// <summary>One "Key = Value" line of UE4SS-settings.ini, with what its comment block says about it.</summary>
/// <param name="Minimum">A number's lowest value, from its comment ("(0-10)", "[slider 1 10 0.5]").</param>
/// <param name="Step">A slider's step; set only when the mod asks for a slider.</param>
/// <param name="NeedsRestart">The comment says the setting applies after a restart ("[restart]", "restart needed").</param>
/// <param name="Hidden">The mod asks for it not to be shown ("[hidden]": an internal or testing value); it stays in the file for hand edits.</param>
/// <param name="ServerDecides">"[server]": in multiplayer only the host's or server's copy is read.</param>
/// <param name="LocalOnly">"[local]": read on each player's own PC (an exception in a file marked [server]).</param>
public sealed record Ue4ssSetting(
    string Section, string Key, string Value, string Description, string? Default, Ue4ssSettingKind Kind, IReadOnlyList<string> Choices,
    double? Minimum = null, double? Maximum = null, double? Step = null, bool NeedsRestart = false, bool Hidden = false,
    bool ServerDecides = false, bool LocalOnly = false);

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

    /// <summary>
    /// A mod's own config: text after " ;" on a setting's line is a comment ("Mode = Forgiving ;
    /// NoStability, Forgiving, Observe or Off"), kept when the value changes and read for choices,
    /// ranges and markers. UE4SS's own settings file has no such comments and is read as before.
    /// </summary>
    private bool InlineComments => _descriptionSeparator == "\n";

    private Ue4ssSettingsDocument(List<string> lines, List<string> endings, string newline, string descriptionSeparator)
    {
        _lines = lines;
        _endings = endings;
        _newline = newline;
        _descriptionSeparator = descriptionSeparator;
    }

    /// <summary>A value and the comment after it on its line: "Forgiving ; NoStability or Off" → ("Forgiving", " ; NoStability or Off"). The comment starts at a ';' after white space (or right at the start).</summary>
    private (string Value, string Comment) SplitInline(string raw)
    {
        if (!InlineComments)
        {
            return (raw, "");
        }

        var at = InlineCommentStart(raw);
        return at >= 0 ? (raw[..at].TrimEnd(), raw[at..]) : (raw, "");
    }

    /// <summary>
    /// Where the comment after a value starts (the white space before its ';'), or -1. A ';' inside
    /// double quotes is part of the value ("Prefix = "[A] ; " ; note").
    /// </summary>
    private static int InlineCommentStart(string raw)
    {
        var inQuotes = false;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (raw[i] == ';' && !inQuotes && (i == 0 || char.IsWhiteSpace(raw[i - 1])))
            {
                var start = i;
                while (start > 0 && char.IsWhiteSpace(raw[start - 1]))
                {
                    start--;
                }

                return start;
            }
        }

        return -1;
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

    /// <summary>Written to a temp file and moved into place, so a crash mid-save can't leave the game's settings cut short.</summary>
    public void Save(string path) => AtomicFile.WriteAllText(path, ToText());

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
                    var (value, comment) = SplitInline(keyMatch.Groups["value"].Value.Trim());
                    var note = comment.Trim().TrimStart(';').Trim();
                    // A setting with its own comment on its line is described by that; it doesn't
                    // borrow the comment above the setting before it (that one was about the other).
                    var block = comments.Count == 0 && afterSetting && _descriptionSeparator == "\n" && note.Length == 0 ? shared : [.. comments];
                    settings.Add(Describe(section, keyMatch.Groups["key"].Value.Trim(), value, block, _descriptionSeparator, note));
                    shared = block;
                    comments.Clear();
                    afterSetting = true;
                }
            }

            // A key listed twice in a section: the last one is what's read (FindLine), so that's the
            // one field shown.
            var last = new Dictionary<(string, string), int>();
            for (var i = 0; i < settings.Count; i++)
            {
                last[(settings[i].Section.ToLowerInvariant(), settings[i].Key.ToLowerInvariant())] = i;
            }

            return last.Count == settings.Count
                ? settings
                : settings.Where((s, i) => last[(s.Section.ToLowerInvariant(), s.Key.ToLowerInvariant())] == i).ToList();
        }
    }

    public string? GetValue(string section, string key) =>
        FindLine(section, key) is { } index ? SplitInline(KeyLine().Match(_lines[index]).Groups["value"].Value.Trim()).Value.Trim() : null;

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
            // The comment after the value (a mod's own config) stays as it is, spacing and all.
            var (current, comment) = SplitInline(match.Groups["value"].Value);
            if (current.Trim() == value)
            {
                return false;
            }

            if (InlineComments && InlineCommentStart(value) >= 0)
            {
                throw new ArgumentException("A value can't contain ' ;': the rest would be read as a comment.", nameof(value));
            }

            var eq = match.Groups["eq"].Value;
            // "Key = ; note" given a value: the note needs white space before its ';' to stay a note.
            if (comment.Length > 0 && value.Length > 0 && !char.IsWhiteSpace(comment[0]))
            {
                comment = " " + comment;
            }

            _lines[index] = $"{match.Groups["lead"].Value}{match.Groups["key"].Value}{(eq.EndsWith(' ') || value.Length == 0 ? eq : eq + " ")}{value}{comment}";
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

    /// <summary>
    /// The key's LAST line in the section (a section listed twice counts as one): a key listed
    /// twice ends up with the last value, both in UE4SS's own ini parser (each line writes into the
    /// same entry of the section's key map, RE-UE4SS IniParser TokenParser.cpp) and in the simple
    /// line-by-line Lua readers mods use for their config. Showing or editing the first would
    /// change a line nothing reads.
    /// </summary>
    private int? FindLine(string section, string key)
    {
        int? found = null;
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
                found = i;
            }
        }

        return found;
    }

    /// <summary>
    /// A setting of a Lua config (config.lua), described the same way as one of an ini: the
    /// comment lines above it and the note after it on its line give the description, the tags
    /// in the note ("[slider 0.5 4 0.5]", "[choice A B C]", "[dropdown A|B]", "[key]", "[restart]",
    /// "[hidden]", as MkHaters Mod Settings reads them) the control. The Lua value's own kind
    /// decides what it is: true/false a switch, a number a number (a slider with the tag), a
    /// string text or a choice, anything else (a table on one line, nil) its Lua text.
    /// </summary>
    public static Ue4ssSetting DescribeLuaSetting(string key, string value, LuaConfigValueKind kind, IReadOnlyList<string> comments, string? note)
    {
        var described = Describe("", key, value, [.. comments], "\n", note ?? "");
        // A key: "R" tagged [key], or a one-line { key = "F4", shift = true } (a key without the tag, as MkHaters Mod Settings reads it).
        var isKey = note is not null && KeyMarker().IsMatch(note);
        var settingKind = kind switch
        {
            LuaConfigValueKind.Boolean => Ue4ssSettingKind.Toggle,
            LuaConfigValueKind.Number => Ue4ssSettingKind.Number,
            LuaConfigValueKind.String when isKey && Ue4ssKeyBind.IsKeyName(value) => Ue4ssSettingKind.Key,
            LuaConfigValueKind.Table when Ue4ssKeyBind.TryParse(value, out _) => Ue4ssSettingKind.Key,
            LuaConfigValueKind.String when described.Choices.Count > 1 => Ue4ssSettingKind.Choice,
            _ => Ue4ssSettingKind.Text,
        };
        var isNumber = settingKind == Ue4ssSettingKind.Number;
        return described with
        {
            Kind = settingKind,
            Choices = settingKind == Ue4ssSettingKind.Choice ? described.Choices : [],
            Minimum = isNumber ? described.Minimum : null,
            Maximum = isNumber ? described.Maximum : null,
            Step = isNumber ? described.Step : null,
        };
    }

    private static Ue4ssSetting Describe(string section, string key, string value, List<string> comments, string separator, string note = "")
    {
        string? defaultValue = null;
        var choices = new List<string>();
        var description = new List<string>();

        // The comment on the setting's own line (a mod's config): its markers, its range, and a
        // list of the values it can take, which becomes the choices.
        double? minimum = null, maximum = null, step = null;
        var needsRestart = false;
        var hidden = false;
        var serverDecides = false;
        var localOnly = false;
        if (note.Length > 0)
        {
            if (SliderMarker().Match(note) is { Success: true } slider)
            {
                (minimum, maximum) = (Number(slider.Groups["min"].Value), Number(slider.Groups["max"].Value));
                step = slider.Groups["step"].Success ? Number(slider.Groups["step"].Value) : null;
                // No step, or a step of 0 (the slider would write "NaN"): whole numbers.
                step = step is > 0 ? step : 1;
            }

            // "[choice A B C]" / "[dropdown Conifer Forest|Arctic]": the values it can take, as the mod lists them.
            if (ChoiceMarker().Match(note) is { Success: true } choiceTag)
            {
                var list = choiceTag.Groups["list"].Value;
                choices.AddRange((list.Contains('|') ? list.Split('|') : list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            }

            hidden = HiddenMarker().IsMatch(note);
            serverDecides = ServerMarker().IsMatch(note);
            localOnly = LocalMarker().IsMatch(note);
            needsRestart = RestartMarker().IsMatch(note) || RestartWords().IsMatch(note);
            note = RestartWords().Replace(Marker().Replace(note, ""), "").Trim().TrimEnd(';', ',').Trim();
            if (minimum is null && RangeInNote().Match(note) is { Success: true } range)
            {
                (minimum, maximum) = (Number(range.Groups["min"].Value), Number(range.Groups["max"].Value));
            }
            else if (minimum is null && AtLeast().Match(note) is { Success: true } atLeast)
            {
                minimum = Number(atLeast.Groups["min"].Value);
            }

            // A range in brackets on its own ("(0-10)", "(at least 10)") shows under the box instead.
            if (minimum is not null && Number(value) is not null)
            {
                note = RangeOnly().Replace(note, "").Trim();
            }

            if (choices.Count == 0 && ChoiceList().IsMatch(note))
            {
                var listed = WordOrList().Split(note).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                if (listed.Count > 1 && listed.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    choices.AddRange(listed);
                    note = ""; // only the list: the drop-down shows it
                }
            }
        }
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

        if (note.Length > 0)
        {
            description.Add(note);
        }

        var text = string.Join(separator, description);
        var isModConfig = separator == "\n";
        if (isModConfig)
        {
            // The comment above, in words (NearbyCrafting.ini: "Valid range: 1.0-500.0 metres",
            // "Changing this setting requires an Icarus restart").
            if (minimum is null && maximum is null && RangeInComment().Match(text) is { Success: true } range)
            {
                (minimum, maximum) = (Number(range.Groups["min"].Value), Number(range.Groups["max"].Value));
            }

            needsRestart = needsRestart || RestartInComment().IsMatch(text);
        }

        var isSwitch = isModConfig
            ? IsModSwitch(key, value, text)
            : IsToggle(value) && (defaultValue is null || IsToggle(defaultValue)) && !NumberDescription().IsMatch(text);
        var kind = choices.Count > 1 ? Ue4ssSettingKind.Choice
            : isSwitch ? Ue4ssSettingKind.Toggle
            : isModConfig && Number(value) is not null ? Ue4ssSettingKind.Number
            : Ue4ssSettingKind.Text;
        return new Ue4ssSetting(section, key, value, text, defaultValue, kind, choices,
            kind == Ue4ssSettingKind.Number ? minimum : null, kind == Ue4ssSettingKind.Number ? maximum : null,
            kind == Ue4ssSettingKind.Number ? step : null, needsRestart, hidden, serverDecides, localOnly);
    }

    // "Valid range: 1.0-500.0 metres", "Range: 0 to 10": a range written out in the comment above.
    [GeneratedRegex(@"\b(?:valid\s+)?range\s*:\s*(?<min>-?\d+(?:\.\d+)?)\s*(?:-|–|to)\s*(?<max>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeInComment();

    // "requires an Icarus restart", "needs a restart", "applies after the game is restarted", "after a restart".
    [GeneratedRegex(@"\b(?:requires?|needs?)\s+(?:an?\s+)?(?:\w+\s+)?restart\b|\bafter\s+(?:a|the)\s+(?:\w+\s+)?(?:is\s+)?restart(?:ed)?\b|\brestart\s+(?:needed|required)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RestartInComment();

    [GeneratedRegex(@"\[key\]", RegexOptions.IgnoreCase)]
    private static partial Regex KeyMarker();

    [GeneratedRegex(@"\[server\]", RegexOptions.IgnoreCase)]
    private static partial Regex ServerMarker();

    [GeneratedRegex(@"\[local\]", RegexOptions.IgnoreCase)]
    private static partial Regex LocalMarker();

    /// <summary>A number as a mod's config writes it ("2", "2.0", "-1"), or null.</summary>
    public static double? Number(string text) =>
        double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && double.IsFinite(n) ? n : null;

    // "[slider 1 10 0.5]": the mod asks for a slider from 1 to 10 in steps of 0.5.
    [GeneratedRegex(@"\[slider\s+(?<min>-?[0-9.]+)\s+(?<max>-?[0-9.]+)(\s+(?<step>[0-9.]+))?\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex SliderMarker();

    [GeneratedRegex(@"\[restart\]", RegexOptions.IgnoreCase)]
    private static partial Regex RestartMarker();

    [GeneratedRegex(@"\[hidden\]", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenMarker();

    // "[choice A B C]", "[dropdown Conifer Forest|Arctic|Desert]": the mod's own list of values.
    [GeneratedRegex(@"\[(choice|dropdown)\s+(?<list>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ChoiceMarker();

    [GeneratedRegex(@"\s*[;,]?\s*\brestart (needed|required)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RestartWords();

    // Any "[word ...]" marker for tools, not for people (the tags MkHaters Mod Settings reads; "[Wax]" is text).
    [GeneratedRegex(@"\s*\[(slider|restart|choice|dropdown|key|hidden|server|local|shared)(\s[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    // "(0-10)", "(1-100)", "(0-600; 0 = off)": the range a number takes.
    [GeneratedRegex(@"\((?<min>-?\d+(\.\d+)?)\s*[-–]\s*(?<max>-?\d+(\.\d+)?)\s*[;),]")]
    private static partial Regex RangeInNote();

    // "(0-10)" or "(at least 10)" with nothing else in the brackets.
    [GeneratedRegex(@"\s*\((at least\s+)?-?\d+(\.\d+)?(\s*[-–]\s*-?\d+(\.\d+)?)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeOnly();

    [GeneratedRegex(@"\bat least (?<min>-?\d+(\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex AtLeast();

    // "Fair or Game", "Reach, Strength or Both", "A, B, C": only words, commas and "or".
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*(\s*,?\s+or\s+[A-Za-z_][A-Za-z0-9_]*)?$")]
    private static partial Regex ChoiceList();

    [GeneratedRegex(@"\s*,\s*or\s+|\s*,\s*|\s+or\s+")]
    private static partial Regex WordOrList();

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
