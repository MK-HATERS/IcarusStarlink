using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Server;

/// <summary>
/// Edits a UE4SS Mods\mods.txt ("Name : 1" per line) in place: comments, blank lines and order kept.
/// A new entry goes ABOVE the built-in keybinds block — the stock file says "; Built-in keybinds,
/// do not move up!" and the Keybinds line must stay last.
/// </summary>
public sealed partial class Ue4ssModsTxt
{
    private readonly List<string> _lines;

    private Ue4ssModsTxt(List<string> lines) => _lines = lines;

    public static Ue4ssModsTxt Parse(string? text) =>
        new(string.IsNullOrEmpty(text) ? [] : [.. text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')]);

    public static Ue4ssModsTxt Load(string path) => Parse(File.Exists(path) ? File.ReadAllText(path) : null);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToString());
    }

    public override string ToString() => _lines.Count == 0 ? "" : string.Join("\r\n", _lines) + "\r\n";

    /// <summary>Every "Name : 0/1" entry, in file order.</summary>
    public IReadOnlyList<(string Name, bool Enabled)> Entries =>
        [.. _lines.Select(l => EntryPattern().Match(l)).Where(m => m.Success).Select(m => (m.Groups["name"].Value.Trim(), m.Groups["on"].Value == "1"))];

    public bool? IsEnabled(string name) =>
        Entries.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).Select(e => (bool?)e.Enabled).FirstOrDefault();

    public void Set(string name, bool enabled)
    {
        var value = enabled ? "1" : "0";
        for (var i = 0; i < _lines.Count; i++)
        {
            var match = EntryPattern().Match(_lines[i]);
            if (match.Success && string.Equals(match.Groups["name"].Value.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{match.Groups["name"].Value.Trim()} : {value}";
                return;
            }
        }

        var insertAt = _lines.FindIndex(l => l.TrimStart().StartsWith("; Built-in keybinds", StringComparison.OrdinalIgnoreCase));
        if (insertAt < 0)
        {
            insertAt = _lines.FindIndex(l => EntryPattern().Match(l) is { Success: true } m && m.Groups["name"].Value.Trim().Equals("Keybinds", StringComparison.OrdinalIgnoreCase));
        }

        if (insertAt < 0)
        {
            _lines.Add($"{name} : {value}");
            return;
        }

        // Above any blank lines that separate the user entries from the keybinds block.
        while (insertAt > 0 && _lines[insertAt - 1].Trim().Length == 0)
        {
            insertAt--;
        }

        _lines.Insert(insertAt, $"{name} : {value}");
    }

    public void Remove(string name) =>
        _lines.RemoveAll(l => EntryPattern().Match(l) is { Success: true } m && string.Equals(m.Groups["name"].Value.Trim(), name, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^\s*(?<name>[^;:\s][^:]*?)\s*:\s*(?<on>[01])\s*$")]
    private static partial Regex EntryPattern();
}
