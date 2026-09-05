namespace IcarusStarlink.Core.Library;

/// <summary>
/// The plain-text mod-list format shared by classic IMM's own LastMergedMods.txt /
/// IMM_Merged_Mod.txt and this app's ISL-Merged.txt — confirmed against the user's real files: an
/// "Includes the following mods:" header line, then one mod display name per line, in merge order.
/// A build with at least one gameplay option enabled (Stacks/Slots/CraftCost/etc.) adds a second,
/// separately-headed section after the mods (RebuildService.WriteManifest) — a real build can have
/// this section with an empty/absent mods section above it (a queue with zero mods, options only).
/// </summary>
public static class ModListText
{
    /// <summary>Both tools write exactly this header — matched tolerantly (a list with no header at all still parses, every line just becomes a name).</summary>
    public const string Header = "Includes the following mods:";

    /// <summary>Marks the start of the gameplay-options section, if present — never emitted by classic IMM's own tools, only by this app's own RebuildService.WriteManifest.</summary>
    public const string OptionsHeader = "Gameplay options applied:";

    /// <summary>
    /// Stops at OptionsHeader rather than treating it (or anything after it) as a mod name — an
    /// option description like "Stacks x2" is not a mod name and must never be fed into
    /// InstalledState.ModNames/InstalledVsListComparer's own name-diffing logic.
    /// </summary>
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
            if (string.Equals(line, OptionsHeader, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            names.Add(line);
        }

        return names;
    }

    /// <summary>The inverse of ParseNames' own OptionsHeader handling — everything after that header, one gameplay-option description per line. Empty if the section is absent (no gameplay option was active for this build).</summary>
    public static IReadOnlyList<string> ParseOptionDescriptions(string content)
    {
        var descriptions = new List<string>();
        var inOptionsSection = false;
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.Equals(line, OptionsHeader, StringComparison.OrdinalIgnoreCase))
            {
                inOptionsSection = true;
                continue;
            }
            if (!inOptionsSection || line.Length == 0)
            {
                continue;
            }

            descriptions.Add(line);
        }

        return descriptions;
    }
}
