namespace IcarusStarlink.Core.Library;

/// <summary>
/// The plain-text mod-list format shared by classic IMM's own LastMergedMods.txt /
/// IMM_Merged_Mod.txt and this app's ISL-Merged.txt — confirmed against the user's real files: an
/// "Includes the following mods:" header line, then one mod display name per line, in merge order.
/// </summary>
public static class ModListText
{
    /// <summary>Both tools write exactly this header — matched tolerantly (a list with no header at all still parses, every line just becomes a name).</summary>
    public const string Header = "Includes the following mods:";

    public static IReadOnlyList<string> ParseNames(string content)
    {
        var names = new List<string>();
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || string.Equals(line, Header, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(line);
        }

        return names;
    }
}
