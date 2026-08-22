namespace IcarusStarlink.Core.Library;

/// <summary>
/// Matches a plain list of mod names (an imported classic-IMM/ISL merge list — see ModListText)
/// against the Library. Confirmed against the user's real LastMergedMods.txt: classic IMM's lists
/// mix genuine display names ("Food Buff Duration - 2x") with folder-style names
/// ("Coracks_Ammo_and_Repair_x100"), so each name is tried against Name, then FolderName, then the
/// EXMOD's own FileName — all case-insensitive exact matches, never fuzzy (a wrong silent guess
/// merging the wrong mod is worse than an honest "couldn't match" the user resolves by hand).
/// </summary>
public static class ModListMatcher
{
    public sealed record Result(IReadOnlyList<LibraryEntry> Matched, IReadOnlyList<string> Unmatched);

    /// <summary>Preserves the list's own order (merge priority) in Matched; a name matching an entry already claimed by an earlier name is treated as a duplicate line and dropped rather than queueing one mod twice.</summary>
    public static Result Match(IReadOnlyList<string> names, IReadOnlyList<LibraryEntry> libraryEntries)
    {
        var matched = new List<LibraryEntry>();
        var unmatched = new List<string>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var entry = FindByName(name, libraryEntries);
            if (entry is null)
            {
                unmatched.Add(name);
            }
            else if (claimed.Add(entry.FolderName))
            {
                matched.Add(entry);
            }
        }

        return new Result(matched, unmatched);
    }

    private static LibraryEntry? FindByName(string name, IReadOnlyList<LibraryEntry> entries) =>
        entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? entries.FirstOrDefault(e => string.Equals(e.FolderName, name, StringComparison.OrdinalIgnoreCase))
        ?? entries.FirstOrDefault(e => string.Equals(e.FileName, name, StringComparison.OrdinalIgnoreCase));
}
