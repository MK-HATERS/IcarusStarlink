using System.Globalization;
using System.Text;

namespace IcarusStarlink.PakIO.ArcFission;

/// <summary>A Lua table read by LuaData: its named fields (name = v, ["name"] = v) and its list part ({ a, b }).</summary>
public sealed class LuaTable
{
    public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);

    public List<object?> Items { get; } = [];

    public string? String(string name) => Fields.GetValueOrDefault(name) as string;

    public LuaTable? Table(string name) => Fields.GetValueOrDefault(name) as LuaTable;
}

/// <summary>
/// Reads a Lua data file (`return { ... }`): tables, strings (quoted or [[long]]), numbers, true,
/// false, nil and comments, nothing else. Never runs code: an expression or a call is a
/// FormatException. Written for ArcFission's mod.lua and rows.lua, which its library loads in an
/// empty environment, so a real one never has more than this.
/// </summary>
public sealed class LuaData
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
                var keyText = key switch
                {
                    string s => s,
                    double d => d.ToString(CultureInfo.InvariantCulture),
                    _ => throw Error("a table key must be text or a number"),
                };
                table.Fields[keyText] = Value(depth);
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
        while (true)
        {
            if (_at >= _text.Length || _text[_at] is '\n' or '\r')
            {
                throw Error("a quoted string isn't closed");
            }

            var c = _text[_at++];
            if (c == quote)
            {
                return text.ToString();
            }

            if (c != '\\')
            {
                text.Append(c);
                continue;
            }

            if (_at >= _text.Length)
            {
                throw Error("a quoted string isn't closed");
            }

            var e = _text[_at++];
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
                case '\n': text.Append('\n'); break;
                default:
                    if (char.IsAsciiDigit(e))
                    {
                        // \ddd: up to three decimal digits, one byte.
                        var digits = e.ToString();
                        while (digits.Length < 3 && _at < _text.Length && char.IsAsciiDigit(_text[_at]))
                        {
                            digits += _text[_at++];
                        }

                        text.Append((char)int.Parse(digits, CultureInfo.InvariantCulture));
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
        // A line break right after the opening bracket isn't part of the string.
        if (_at < _text.Length && _text[_at] == '\r')
        {
            _at++;
        }

        if (_at < _text.Length && _text[_at] == '\n')
        {
            _at++;
        }

        var close = "]" + new string('=', level) + "]";
        var end = _text.IndexOf(close, _at, StringComparison.Ordinal);
        if (end < 0)
        {
            throw Error("a [[long string]] isn't closed");
        }

        var value = _text[_at..end];
        _at = end + close.Length;
        return value;
    }

    private double Number()
    {
        var start = _at;
        if (_text[_at] == '-')
        {
            _at++;
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

    private void SkipSpace()
    {
        while (_at < _text.Length)
        {
            if (char.IsWhiteSpace(_text[_at]))
            {
                _at++;
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
                    while (_at < _text.Length && _text[_at] != '\n')
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
