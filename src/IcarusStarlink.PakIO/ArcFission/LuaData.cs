using System.Globalization;
using System.Text;

namespace IcarusStarlink.PakIO.ArcFission;

/// <summary>
/// A Lua table read by LuaData: its text-keyed fields (name = v, ["name"] = v), its number-keyed
/// fields ([1] = v) and its list part ({ a, b }). Text and number keys are kept apart as Lua keeps
/// them: ["1"] and [1] are two different keys, and only [1] is part of the list ipairs reads.
/// </summary>
public sealed class LuaTable
{
    /// <summary>Fields with a text key: name = v and ["name"] = v (["1"] too: that's text, not list entry 1).</summary>
    public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);

    /// <summary>Fields with a number key: [1] = v, [2.5] = v.</summary>
    public Dictionary<double, object?> NumberFields { get; } = [];

    public List<object?> Items { get; } = [];

    public string? String(string name) => Fields.GetValueOrDefault(name) as string;

    public LuaTable? Table(string name) => Fields.GetValueOrDefault(name) as LuaTable;

    /// <summary>
    /// The list as Lua's ipairs sees it: entries 1, 2, 3… whether written in order ({ a, b }) or
    /// numbered ({ [1] = a, [2] = b }, how ArcFission Library itself writes lists), up to the first
    /// nil or gap.
    /// </summary>
    public IEnumerable<object?> Sequence()
    {
        for (var i = 1; ; i++)
        {
            var value = i <= Items.Count ? Items[i - 1] : NumberFields.GetValueOrDefault(i);
            if (value is null)
            {
                yield break;
            }

            yield return value;
        }
    }
}

/// <summary>
/// Reads a Lua data file (`return { ... }`): tables, strings (quoted or [[long]]), numbers, true,
/// false, nil and comments, nothing else. Never runs code: an expression or a call is a
/// FormatException. Written for ArcFission's mod.lua and rows.lua, which its library loads in an
/// empty environment, so a real one never has more than this.
/// </summary>
public sealed partial class LuaData
{
    private readonly string _text;
    private int _at;

    private LuaData(string text) => _text = text;

    /// <summary>The value the file returns (a LuaTable, string, double, bool or null).</summary>
    public static object? Parse(string text)
    {
        var reader = new LuaData(text);
        reader.SkipSpace();
        if (!reader.TryWord("return"))
        {
            throw new FormatException("A Lua data file starts with 'return'.");
        }

        var value = reader.Value(depth: 0);
        reader.SkipSpace();
        reader.TryChar(';');
        reader.SkipSpace();
        if (reader._at < reader._text.Length)
        {
            throw reader.Error("unexpected text after the returned value");
        }

        return value;
    }

    private const int MaxDepth = 64;

    private object? Value(int depth)
    {
        SkipSpace();
        if (_at >= _text.Length)
        {
            throw Error("the file ends where a value should be");
        }

        var c = _text[_at];
        if (c == '{')
        {
            return Table(depth + 1);
        }

        if (c is '"' or '\'')
        {
            return QuotedString();
        }

        if (c == '[' && LongBracketLevel() is { } level)
        {
            return LongString(level);
        }

        if (c is '-' or '.' || char.IsAsciiDigit(c))
        {
            return Number();
        }

        if (TryWord("true"))
        {
            return true;
        }

        if (TryWord("false"))
        {
            return false;
        }

        if (TryWord("nil"))
        {
            return null;
        }

        throw Error($"'{c}' doesn't start a value");
    }

    private LuaTable Table(int depth)
    {
        if (depth > MaxDepth)
        {
            throw Error("tables nested too deep");
        }

        _at++; // {
        var table = new LuaTable();
        while (true)
        {
            SkipSpace();
            if (TryChar('}'))
            {
                return table;
            }

            if (_at < _text.Length && _text[_at] == '[' && LongBracketLevel() is null)
            {
                // [key] = value
                _at++;
                var key = Value(depth);
                SkipSpace();
                Expect(']');
                SkipSpace();
                Expect('=');
                switch (key)
                {
                    case string s:
                        table.Fields[s] = Value(depth);
                        break;
                    case double d:
                        table.NumberFields[d] = Value(depth);
                        break;
                    default:
                        throw Error("a table key must be text or a number");
                }
            }
            else if (Name() is { } name)
            {
                SkipSpace();
                Expect('=');
                table.Fields[name] = Value(depth);
            }
            else
            {
                table.Items.Add(Value(depth));
            }

            SkipSpace();
            if (!TryChar(',') && !TryChar(';'))
            {
                SkipSpace();
                Expect('}');
                return table;
            }
        }
    }

    /// <summary>An identifier followed by '=' (a field name); the position is left alone when it isn't one.</summary>
    private string? Name()
    {
        var start = _at;
        if (_at >= _text.Length || !(char.IsAsciiLetter(_text[_at]) || _text[_at] == '_'))
        {
            return null;
        }

        while (_at < _text.Length && (char.IsAsciiLetterOrDigit(_text[_at]) || _text[_at] == '_'))
        {
            _at++;
        }

        var name = _text[start.._at];
        var afterName = _at;
        SkipSpace();
        if (_at < _text.Length && _text[_at] == '=' && name is not ("true" or "false" or "nil"))
        {
            _at = afterName;
            return name;
        }

        _at = start;
        return null;
    }

    private string QuotedString()
    {
        var quote = _text[_at++];
        var text = new StringBuilder();
        // A Lua string is bytes: "\xC3\xA9" (or "\195\169") is é written as its two UTF-8 bytes.
        // Escaped bytes are gathered and decoded as UTF-8 together, never one character each.
        var bytes = new List<byte>();
        void Flush()
        {
            if (bytes.Count > 0)
            {
                text.Append(Encoding.UTF8.GetString([.. bytes]));
                bytes.Clear();
            }
        }

        while (true)
        {
            if (_at >= _text.Length || _text[_at] is '\n' or '\r')
            {
                throw Error("a quoted string isn't closed");
            }

            var c = _text[_at++];
            if (c == quote)
            {
                Flush();
                return text.ToString();
            }

            if (c != '\\')
            {
                Flush();
                text.Append(c);
                continue;
            }

            if (_at >= _text.Length)
            {
                throw Error("a quoted string isn't closed");
            }

            var e = _text[_at++];
            if (e is not ('x' or 'z') && !char.IsAsciiDigit(e))
            {
                Flush();
            }

            switch (e)
            {
                case 'n': text.Append('\n'); break;
                case 't': text.Append('\t'); break;
                case 'r': text.Append('\r'); break;
                case 'a': text.Append('\a'); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'v': text.Append('\v'); break;
                case '\\' or '"' or '\'': text.Append(e); break;
                case '\n':
                    // A backslash before a line break: one line break (\n\r is one, as in Lua).
                    TryChar('\r');
                    text.Append('\n');
                    break;
                case '\r':
                    // A backslash before a Windows line break: one line break.
                    TryChar('\n');
                    text.Append('\n');
                    break;
                case 'z':
                    // \z skips the white space (line breaks too) that follows: Lua's, ASCII only.
                    while (_at < _text.Length && IsLuaSpace(_text[_at]))
                    {
                        _at++;
                    }

                    break;
                case 'x':
                    if (_at + 2 > _text.Length || !char.IsAsciiHexDigit(_text[_at]) || !char.IsAsciiHexDigit(_text[_at + 1]))
                    {
                        throw Error("'\\x' needs two hex digits");
                    }

                    bytes.Add(byte.Parse(_text.AsSpan(_at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    _at += 2;
                    break;
                case 'u':
                    // \u{XXXX}: a Unicode character.
                    var close = _text.IndexOf('}', _at);
                    if (!TryChar('{') || close < 0 || close - _at is < 1 or > 8
                        || !int.TryParse(_text.AsSpan(_at, close - _at), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                        || code is < 0 or > 0x10FFFF)
                    {
                        throw Error("'\\u' needs {hex digits}");
                    }

                    text.Append(code is >= 0xD800 and <= 0xDFFF ? "�" : char.ConvertFromUtf32(code));
                    _at = close + 1;
                    break;
                default:
                    if (char.IsAsciiDigit(e))
                    {
                        // \ddd: up to three decimal digits, one byte (Lua refuses more than 255).
                        var digits = e.ToString();
                        while (digits.Length < 3 && _at < _text.Length && char.IsAsciiDigit(_text[_at]))
                        {
                            digits += _text[_at++];
                        }

                        var value = int.Parse(digits, CultureInfo.InvariantCulture);
                        if (value > 255)
                        {
                            throw Error($"'\\{digits}' is more than one byte");
                        }

                        bytes.Add((byte)value);
                        break;
                    }

                    throw Error($"unknown escape '\\{e}'");
            }
        }
    }

    /// <summary>At '[': the level of a long bracket ([[ is 0, [==[ is 2), or null when it's a plain '['.</summary>
    private int? LongBracketLevel()
    {
        var i = _at + 1;
        var level = 0;
        while (i < _text.Length && _text[i] == '=')
        {
            level++;
            i++;
        }

        return i < _text.Length && _text[i] == '[' ? level : null;
    }

    private string LongString(int level)
    {
        _at += level + 2;
        var close = "]" + new string('=', level) + "]";
        var end = _text.IndexOf(close, _at, StringComparison.Ordinal);
        if (end < 0)
        {
            throw Error("a [[long string]] isn't closed");
        }

        // As Lua reads it: each line break (\r\n, \n\r, \r or \n) is one \n, and a line break right
        // after the opening bracket isn't part of the string.
        var value = LineBreak().Replace(_text[_at..end], "\n");
        _at = end + close.Length;
        return value.StartsWith('\n') ? value[1..] : value;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\r\n|\n\r|\r|\n")]
    private static partial System.Text.RegularExpressions.Regex LineBreak();

    private double Number()
    {
        var start = _at;
        if (_text[_at] == '-')
        {
            _at++;
        }

        // A number starts with a digit or a point: "-Infinity" or "-nan" is a word, which .NET
        // would read as a number but Lua doesn't.
        if (_at >= _text.Length || !(char.IsAsciiDigit(_text[_at]) || _text[_at] == '.'))
        {
            throw Error("a number starts with a digit or a point");
        }

        while (_at < _text.Length)
        {
            var c = _text[_at];
            // Digits, a point, hex letters, and a sign only right after an exponent's e (1e-5).
            if (char.IsAsciiLetterOrDigit(c) || c == '.' || (c is '+' or '-' && _text[_at - 1] is 'e' or 'E'))
            {
                _at++;
                continue;
            }

            break;
        }

        var token = _text[start.._at];
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        var hex = token.StartsWith('-') ? token[1..] : token;
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var whole))
        {
            return token.StartsWith('-') ? -whole : whole;
        }

        throw Error($"'{token}' isn't a number");
    }

    /// <summary>The white space Lua's own reader skips: ASCII only (a non-breaking space is not one).</summary>
    private static bool IsLuaSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private void SkipSpace()
    {
        while (_at < _text.Length)
        {
            var c = _text[_at];
            if (IsLuaSpace(c))
            {
                _at++;
            }
            else if (c > '\x7F' && (char.IsWhiteSpace(c) || c is '﻿' or '​'))
            {
                // Lua's load() (how ArcFission Library reads these files) refuses any byte above 127
                // outside a string or comment: a file with one doesn't load in game at all.
                var what = c switch
                {
                    ' ' => "a non-breaking space",
                    '﻿' => "a hidden byte-order mark",
                    '​' => "a zero-width space",
                    _ => "a non-ASCII space",
                };
                throw Error($"{what} (U+{(int)c:X4}) outside a string, which the ArcFission Library can't read either");
            }
            else if (_at + 1 < _text.Length && _text[_at] == '-' && _text[_at + 1] == '-')
            {
                _at += 2;
                if (_at < _text.Length && _text[_at] == '[' && LongBracketLevel() is { } level)
                {
                    LongString(level);
                }
                else
                {
                    // A line comment ends at a line break, a lone \r included (as Lua's).
                    while (_at < _text.Length && _text[_at] is not ('\n' or '\r'))
                    {
                        _at++;
                    }
                }
            }
            else
            {
                return;
            }
        }
    }

    private bool TryWord(string word)
    {
        if (string.CompareOrdinal(_text, _at, word, 0, word.Length) == 0
            && (_at + word.Length >= _text.Length || !(char.IsAsciiLetterOrDigit(_text[_at + word.Length]) || _text[_at + word.Length] == '_')))
        {
            _at += word.Length;
            return true;
        }

        return false;
    }

    private bool TryChar(char c)
    {
        if (_at < _text.Length && _text[_at] == c)
        {
            _at++;
            return true;
        }

        return false;
    }

    private void Expect(char c)
    {
        if (!TryChar(c))
        {
            throw Error($"'{c}' expected");
        }
    }

    private FormatException Error(string what)
    {
        var line = 1 + _text.AsSpan(0, Math.Min(_at, _text.Length)).Count('\n');
        return new FormatException($"Lua data, line {line}: {what}.");
    }
}
