using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The three kinds of config file (ini, Lua, plain text) behind one IConfigText shape, and the server-file tag.</summary>
public sealed partial class ModConfigEditorViewModel
{
    private static IConfigText Parse(Ue4ssModConfigFormat format, string text) => format switch
    {
        Ue4ssModConfigFormat.Ini => new IniText(Ue4ssSettingsDocument.Parse(text, keepCommentLines: true)),
        Ue4ssModConfigFormat.Lua => new LuaText(LuaConfigDocument.Parse(text)),
        _ => new PlainText(text),
    };

    /// <summary>The three kinds of config file behind one shape: settings to show, a way to change one, and the text.</summary>
    private interface IConfigText
    {
        IReadOnlyList<Ue4ssSetting> Settings { get; }

        string? Get(string section, string key);

        /// <summary>Null when it worked, else why not.</summary>
        string? Set(string section, string key, string value);

        string ToText();

        /// <summary>The heading a setting is listed under: its [Section], or the Lua comment heading above it.</summary>
        string GroupOf(string section, string key) => section;

        /// <summary>A short note on how to write the value, when the box needs one (a Lua list).</summary>
        string? HintOf(string key) => null;
    }

    // "; [MkHatersSettings] Stability [server]" / "-- [MkHatersSettings] Wildlife [server]": the mod's whole file is read where the world runs.
    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*(?:--|;|#).*\[MkHatersSettings\][^\r\n]*\[server\]", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FileWideServerTag();

    private sealed class IniText(Ue4ssSettingsDocument document) : IConfigText
    {
        public IReadOnlyList<Ue4ssSetting> Settings => document.Settings;

        public string? Get(string section, string key) => document.GetValue(section, key);

        public string? Set(string section, string key, string value)
        {
            try
            {
                document.SetValue(section, key, value);
                return null;
            }
            catch (ArgumentException ex)
            {
                return $"{key}: {ex.Message}";
            }
        }

        public string ToText() => document.ToText();
    }

    private sealed class LuaText(LuaConfigDocument document) : IConfigText
    {
        // Numbers, sliders, choices, [restart] and [hidden] from the comments, as MkHaters Mod Settings reads them.
        public IReadOnlyList<Ue4ssSetting> Settings =>
            [.. document.Settings.Select(s => Ue4ssSettingsDocument.DescribeLuaSetting(s.Key, s.Value, s.Kind, s.Comments, s.Note))];

        public string? Get(string section, string key) => document.GetValue(key);

        public string? Set(string section, string key, string value)
        {
            _byKey = null;
            return document.SetValue(key, value);
        }

        public string ToText() => document.ToText();

        // The settings by key, read once per change (GroupOf/HintOf run for every field).
        private Dictionary<string, LuaConfigSetting>? _byKey;

        private LuaConfigSetting? Raw(string key)
        {
            _byKey ??= document.Settings.GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return _byKey.GetValueOrDefault(key);
        }

        public string GroupOf(string section, string key) => Raw(key)?.Group ?? "";

        public string? HintOf(string key) =>
            Raw(key) is { Kind: LuaConfigValueKind.Table } s && !Ue4ssKeyBind.TryParse(s.Value, out _)
                ? "A Lua list: keep the { } and the commas, and names in quotes."
                : null;
    }

    private sealed class PlainText(string text) : IConfigText
    {
        public IReadOnlyList<Ue4ssSetting> Settings => [];

        public string? Get(string section, string key) => null;

        public string? Set(string section, string key, string value) => $"{key} isn't in this file";

        public string ToText() => text;
    }
}
