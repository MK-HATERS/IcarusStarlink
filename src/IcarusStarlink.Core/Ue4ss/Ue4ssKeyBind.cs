using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// A key setting the way MkHaters mods write it on one line: <c>{ key = "F4", ctrl = true }</c>
/// (only key, ctrl, shift and alt). Read from the setting's Lua text and written back in the same
/// shape; anything else in the table makes it not a key (it stays a plain Lua text box).
/// </summary>
public sealed partial record Ue4ssKeyBind(string Key, bool Ctrl, bool Shift, bool Alt)
{
    /// <summary>Longer than any real key table: a bigger one isn't read at all.</summary>
    private const int MaxLength = 200;

    public static bool TryParse(string lua, out Ue4ssKeyBind bind)
    {
        bind = new Ue4ssKeyBind("", false, false, false);
        var text = lua.Trim();
        if (text.Length > MaxLength || text.Length < 2 || text[0] != '{' || text[^1] != '}')
        {
            return false;
        }

        string? key = null;
        bool ctrl = false, shift = false, alt = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = text[1..^1].Split(',');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.Length == 0)
            {
                if (i == parts.Length - 1 && i > 0)
                {
                    continue; // a trailing comma
                }

                return false;
            }

            if (Field().Match(part) is not { Success: true } m || !seen.Add(m.Groups["name"].Value))
            {
                return false;
            }

            var name = m.Groups["name"].Value;
            var value = m.Groups["value"].Value;
            if (name == "key")
            {
                if (value.Length < 2 || value[0] != '"' || value[^1] != '"' || !KeyName().IsMatch(value[1..^1]))
                {
                    return false;
                }

                key = value[1..^1];
            }
            else
            {
                if (value is not ("true" or "false"))
                {
                    return false;
                }

                var on = value == "true";
                switch (name)
                {
                    case "ctrl":
                        ctrl = on;
                        break;
                    case "shift":
                        shift = on;
                        break;
                    default:
                        alt = on;
                        break;
                }
            }
        }

        if (key is null)
        {
            return false;
        }

        bind = new Ue4ssKeyBind(key, ctrl, shift, alt);
        return true;
    }

    /// <summary>"{ key = "F4", ctrl = true }": only the modifiers that are on.</summary>
    public string ToLua()
    {
        var parts = new List<string> { $"key = \"{Key}\"" };
        if (Ctrl)
        {
            parts.Add("ctrl = true");
        }

        if (Shift)
        {
            parts.Add("shift = true");
        }

        if (Alt)
        {
            parts.Add("alt = true");
        }

        return "{ " + string.Join(", ", parts) + " }";
    }

    /// <summary>"Ctrl+F4", "F2".</summary>
    public override string ToString() =>
        string.Concat(Ctrl ? "Ctrl+" : "", Shift ? "Shift+" : "", Alt ? "Alt+" : "", Key);

    /// <summary>An Unreal key name as mods write it: letters, digits and _ ("F4", "NumPadOne", "Equals"); empty for no key.</summary>
    public static bool IsKeyName(string name) => KeyName().IsMatch(name);

    [GeneratedRegex(@"^(?<name>key|ctrl|shift|alt)\s*=\s*(?<value>""[^""\\]*""|true|false)$")]
    private static partial Regex Field();

    [GeneratedRegex(@"^[A-Za-z0-9_]{0,40}$")]
    private static partial Regex KeyName();
}
