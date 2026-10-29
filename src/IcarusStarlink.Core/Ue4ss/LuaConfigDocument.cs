using System.Text;
using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>What a Lua config value is, which decides how it's shown and written back.</summary>
public enum LuaConfigValueKind
{
    Boolean,
    Number,

    /// <summary>Shown without its quotes; written back in the quotes the file used.</summary>
    String,

    /// <summary>A table written on one line ("{ "Wolf", "Bear" }"), edited as its Lua text.</summary>
    Table,

    /// <summary>nil, or anything else: edited as its Lua text.</summary>
    Other,
}

/// <summary>One "Key = value" line of a Lua config, with the comment above it (and after it on the line).</summary>
/// <param name="Description">The comment lines above the setting and the one after it on its line, joined with line breaks.</param>
/// <param name="Comments">The comment lines right above the setting (a note about it), without the "--".</param>
/// <param name="Note">The comment after the value on the setting's own line, without the "--"; null when there is none. Where MkHaters mods put their tags ("[slider 1 10]", "[restart]").</param>
public sealed record LuaConfigSetting(string Key, string Value, LuaConfigValueKind Kind, string Description, IReadOnlyList<string> Comments, string? Note)
{
    public LuaConfigSetting(string key, string value, LuaConfigValueKind kind, string description)
        : this(key, value, kind, description, description.Length == 0 ? [] : description.Split('\n'), null)
    {
    }

    /// <summary>The comment heading the setting sits under ("-- ==== SPAWN AMOUNTS ====" → "SPAWN AMOUNTS"), as written; empty before the first one.</summary>
    public string Group { get; init; } = "";
}

/// <summary>
/// A UE4SS Lua mod's config file ("return { Enabled = true, ... }" or "Config.Enabled = true"),
/// read and written line by line, so saving only changes the value part of the edited lines:
/// comments, layout and everything the editor doesn't understand stay exactly as they are.
/// The settings are the one-line "Key = value" lines at the top level of the file or of its
/// top-level table; anything deeper (a list of tables like Herds = { ... } over several lines)
/// is left to the plain-text editor.
/// </summary>
public sealed partial class LuaConfigDocument
{
    private readonly List<string> _lines;
    private readonly List<string> _endings;
    private readonly List<Entry> _entries;

    private sealed record Entry(int Line, string Key, int ValueStart, int ValueLength, LuaConfigValueKind Kind, char Quote, IReadOnlyList<string> Comments, string? Note, string Group)
    {
        public string Description => string.Join("\n", Note is { Length: > 0 } ? Comments.Append(Note) : Comments);
    }

    private LuaConfigDocument(List<string> lines, List<string> endings)
    {
        _lines = lines;
        _endings = endings;
        _entries = Scan(lines, out var unknown);
        UnknownLineCount = unknown;
    }

    /// <summary>
    /// Lines that aren't a setting, a comment, blank, or the table's own braces: the inside of a
    /// table over several lines (Wildlife's Herds), code, anything else. A file with none of these
    /// is entirely settings, so it can be rebuilt on another file's layout without losing a thing.
    /// </summary>
    public int UnknownLineCount { get; }

    public static LuaConfigDocument Parse(string text)
    {
        var (lines, endings) = TextLines.Split(text);
        return new LuaConfigDocument(lines, endings);
    }

    public string ToText() => TextLines.Join(_lines, _endings);

    public IReadOnlyList<LuaConfigSetting> Settings =>
        [.. _entries.Select(e => new LuaConfigSetting(e.Key, DisplayValue(e), e.Kind, e.Description, e.Comments, e.Note) { Group = e.Group })];

    public string? GetValue(string key) => Find(key) is { } e ? DisplayValue(e) : null;

    /// <summary>
    /// Changes one value in place. <paramref name="value"/> is what the editor shows (a string
    /// without its quotes). Null when it worked (or nothing changed), else why it can't be written.
    /// A key the file doesn't have is an error: where it would go in a Lua file is the mod's call.
    /// </summary>
    public string? SetValue(string key, string value)
    {
        if (Find(key) is not { } entry)
        {
            return $"{key} isn't in this file";
        }

        // An unchanged value is left exactly as written: the editor sends every field on Save, and
        // re-writing an untouched string must not respell its escapes.
        if (DisplayValue(entry) == value)
        {
            return null;
        }

        if (Format(entry.Kind, entry.Quote, value) is not { } text)
        {
            return $"{key}: {ExpectedText(entry.Kind)}";
        }

        var line = _lines[entry.Line];
        if (line.Substring(entry.ValueStart, entry.ValueLength) == text)
        {
            return null;
        }

        _lines[entry.Line] = line[..entry.ValueStart] + text + line[(entry.ValueStart + entry.ValueLength)..];
        var index = _entries.IndexOf(entry);
        _entries[index] = entry with { ValueLength = text.Length };
        return null;
    }

    private static string ExpectedText(LuaConfigValueKind kind) => kind switch
    {
        LuaConfigValueKind.Boolean => "needs true or false",
        LuaConfigValueKind.Number => "needs a number (like 5, 0.5 or -2)",
        LuaConfigValueKind.Table => "needs a list in { }, with every quote and bracket closed",
        _ => "can't span lines",
    };

    private Entry? Find(string key) => _entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.Ordinal));

    private string DisplayValue(Entry e)
    {
        var raw = _lines[e.Line].Substring(e.ValueStart, e.ValueLength);
        return e.Kind == LuaConfigValueKind.String ? Unescape(raw[1..^1]) : raw;
    }

    private static string? Format(LuaConfigValueKind kind, char quote, string value)
    {
        // A string writes a line break as \n (see Escape); no other kind can hold one.
        if (kind != LuaConfigValueKind.String && (value.Contains('\n') || value.Contains('\r')))
        {
            return null;
        }

        var trimmed = value.Trim();
        switch (kind)
        {
            case LuaConfigValueKind.Boolean:
                return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true"
                    : trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ? "false" : null;
            case LuaConfigValueKind.Number:
                return NumberText().IsMatch(trimmed) ? trimmed : null;
            case LuaConfigValueKind.String:
                return quote + Escape(value, quote) + quote;
            case LuaConfigValueKind.Table:
                return trimmed.StartsWith('{') && ScanValue(trimmed, 0) == trimmed.Length ? trimmed : null;
            default:
                return trimmed.Length > 0 && ScanValue(trimmed, 0) == trimmed.Length ? trimmed : null;
        }
    }

    /// <summary>The inverse of Unescape: a backslash, the quote, and line break / carriage return / tab are written as Lua escapes.</summary>
    private static string Escape(string value, char quote)
    {
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            switch (c)
            {
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c == '\\' || c == quote)
                    {
                        sb.Append('\\');
                    }

                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// What the editor shows for a quoted Lua string: \n, \r, \t, \\ and the escaped quotes become
    /// the characters they stand for. Any other escape (a decimal \065, a hex \x41, \z …) is shown
    /// as written, backslash included — it used to lose the backslash, so "\065" showed as "065".
    /// </summary>
    private static string Unescape(string raw)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                i++;
                switch (raw[i])
                {
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case '\\' or '"' or '\'':
                        sb.Append(raw[i]);
                        break;
                    default:
                        sb.Append('\\').Append(raw[i]);
                        break;
                }

                continue;
            }

            sb.Append(raw[i]);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^-?(0[xX][0-9a-fA-F]+|(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?)$")]
    private static partial Regex NumberText();

    [GeneratedRegex(@"^(?<lead>\s*(?:local\s+)?)(?<key>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*=(?!=)\s*")]
    private static partial Regex KeyStart();

    // "return {", "local Config = {", "Config = {", "}" or "}," (optionally with a comment after): the table's own lines.
    [GeneratedRegex(@"^(?:(?:return\s*|(?:local\s+)?[A-Za-z_][A-Za-z0-9_.]*\s*=\s*)?\{|\}[,;]?)\s*(?:--.*)?$")]
    private static partial Regex TableBrace();

    /// <summary>A heading longer than this is cut: it's a name, not a paragraph.</summary>
    private const int MaxGroupLength = 80;

    /// <summary>A comment longer than this is never a heading (and isn't matched as one).</summary>
    private const int MaxHeadingLineLength = 200;

    // "==== GENERAL ====", "--- Keys ---", "#### Log ####", "======" (a bare rule): the comment text of a heading line.
    [GeneratedRegex(@"^(?:(?>[=#*~-]{3,})\s*(?<name>[^=#*~]*?)\s*[=#*~-]{3,}|[=#*~-]{3,})$")]
    private static partial Regex Heading();

    private static List<Entry> Scan(List<string> lines, out int unknownLines)
    {
        var entries = new List<Entry>();
        var comments = new List<string>();
        var depth = 0;
        var inBlockComment = false;
        var group = "";
        unknownLines = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (inBlockComment)
            {
                inBlockComment = !trimmed.Contains("]]");
                continue;
            }

            if (trimmed.StartsWith("--[["))
            {
                inBlockComment = !trimmed[4..].Contains("]]");
                comments.Clear();
                continue;
            }

            if (trimmed.StartsWith("--"))
            {
                // A heading ("-- ==== SPAWN AMOUNTS ====") starts a group of settings; it isn't part of
                // the next setting's description. A bare rule ("-- ======") is dropped too.
                var comment = trimmed[2..].Trim();
                // Length-capped: a downloaded file's long run of dashes must not make the match slow.
                if (depth <= 1 && comment.Length <= MaxHeadingLineLength && Heading().Match(comment) is { Success: true } heading)
                {
                    var name = heading.Groups["name"].Value.Trim();
                    if (name.Any(char.IsLetter))
                    {
                        group = name.Length > MaxGroupLength ? name[..MaxGroupLength] : name;
                    }

                    comments.Clear();
                    continue;
                }

                comments.Add(comment);
                continue;
            }

            if (trimmed.Length == 0)
            {
                comments.Clear();
                continue;
            }

            if (depth <= 1 && TryReadSetting(line, out var key, out var start, out var length, out var kind, out var quote, out var inline))
            {
                entries.Add(new Entry(i, key, start, length, kind, quote, [.. comments], inline, group));
            }
            else if (!(depth <= 1 && TableBrace().IsMatch(trimmed)))
            {
                unknownLines++;
            }

            comments.Clear();
            depth = Math.Max(0, depth + BraceChange(line));
        }

        return entries;
    }

    private static bool TryReadSetting(string line, out string key, out int start, out int length, out LuaConfigValueKind kind, out char quote, out string? inlineComment)
    {
        key = "";
        start = length = 0;
        kind = LuaConfigValueKind.Other;
        quote = '"';
        inlineComment = null;
        if (KeyStart().Match(line) is not { Success: true } m)
        {
            return false;
        }

        start = m.Length;
        var end = ScanValue(line, start);
        if (end <= start)
        {
            return false;
        }

        // After the value: an optional separator, then an optional comment, then nothing.
        var rest = line[end..].TrimStart();
        if (rest.StartsWith(',') || rest.StartsWith(';'))
        {
            rest = rest[1..].TrimStart();
        }

        if (rest.StartsWith("--"))
        {
            inlineComment = rest[2..].Trim();
        }
        else if (rest.Length > 0)
        {
            return false; // "x = a + b", "f = function() ...": not a plain setting
        }

        key = m.Groups["key"].Value;
        length = end - start;
        var raw = line.Substring(start, length);
        if (raw is "true" or "false")
        {
            kind = LuaConfigValueKind.Boolean;
        }
        else if (NumberText().IsMatch(raw))
        {
            kind = LuaConfigValueKind.Number;
        }
        else if (raw[0] is '"' or '\'')
        {
            kind = LuaConfigValueKind.String;
            quote = raw[0];
        }
        else if (raw[0] == '{')
        {
            if (raw[1..^1].Trim().Length == 0)
            {
                return false; // "local Config = {}": the table the settings go into, not a setting
            }

            kind = LuaConfigValueKind.Table;
        }
        else if (raw != "nil")
        {
            return false; // a name or call ("Key.F1", require(...)): not something to edit as a value
        }

        return true;
    }

    /// <summary>The end of the one Lua value that starts at <paramref name="start"/> (a string, a table closed on the same line, or a word/number), or start when there is none.</summary>
    private static int ScanValue(string line, int start)
    {
        if (start >= line.Length)
        {
            return start;
        }

        var c = line[start];
        if (c is '"' or '\'')
        {
            return StringEnd(line, start) is { } e ? e : start;
        }

        if (c == '{')
        {
            var depth = 0;
            for (var i = start; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch is '"' or '\'')
                {
                    if (StringEnd(line, i) is not { } e)
                    {
                        return start;
                    }

                    i = e - 1;
                }
                else if (ch == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    return start; // a comment inside an unclosed table
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}' && --depth == 0)
                {
                    return i + 1;
                }
            }

            return start; // goes on over the next lines
        }

        var j = start;
        while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] is '_' or '.' or '-' or '+') && !(line[j] == '-' && j + 1 < line.Length && line[j + 1] == '-'))
        {
            j++;
        }

        return j;
    }

    private static int? StringEnd(string line, int open)
    {
        var quote = line[open];
        for (var i = open + 1; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
            }
            else if (line[i] == quote)
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>Opening minus closing braces on a line, outside strings and comments.</summary>
    private static int BraceChange(string line)
    {
        var change = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch is '"' or '\'')
            {
                if (StringEnd(line, i) is not { } e)
                {
                    break;
                }

                i = e - 1;
            }
            else if (ch == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                break;
            }
            else if (ch == '{')
            {
                change++;
            }
            else if (ch == '}')
            {
                change--;
            }
        }

        return change;
    }
}
