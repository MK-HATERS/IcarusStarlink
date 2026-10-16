using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Safety;

/// <summary>
/// "Was this pak packed right?" — checked once at import, on the pak's files as the game sees them
/// (already restaged under Content, see PakExtraction). Every finding here came from real Nexus
/// downloads: an author's whole .git folder, Notepad++ session and build script packed into the
/// mod; a blueprint dumped to .json where the game never reads it; full table copies built against
/// an older game week; tables shipped without a single change. All are warnings, shown at import:
/// some are harmless inside Starlink, but each says the pak was packed carelessly or is outdated.
/// </summary>
public static class PakContentLints
{
    private static readonly HashSet<string> GameFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uasset", ".uexp", ".ubulk", ".umap", ".uptnl", ".locres", ".ushaderbytecode", ".ufont", ".bnk", ".wem",
    };

    public static void Inspect(string extractedUnderContent, string dataFolder, string pakName, IReadOnlyList<string> outsideContent, MergeReport report)
    {
        if (!Directory.Exists(extractedUnderContent))
        {
            return;
        }

        var outside = outsideContent.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(extractedUnderContent, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(extractedUnderContent, f).Replace('\\', '/'))
            .Where(f => !outside.Contains(f))
            .ToList();

        var junk = files.Where(IsUnusedByTheGame).OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (junk.Count > 0)
        {
            report.AddWarning(
                $"'{pakName}' has {junk.Count} file(s) packed inside it that the game never uses (e.g. {string.Join(", ", junk.Take(3))}). "
                + "Harmless, but a sign the pak was packed carelessly — files like these are not part of the mod.");
        }

        if (outsideContent.Count > 0)
        {
            report.AddWarning(
                $"'{pakName}' has {outsideContent.Count} file(s) outside the game's Content folder (e.g. {outsideContent[0]}). "
                + "They can't load from where the pak puts them.");
        }

        InspectTables(extractedUnderContent, files, dataFolder, pakName, report);
    }

    /// <summary>
    /// The author's build leftovers — a git repository, editor session files, build scripts — never
    /// part of a mod, so a pak converted into a Library mod leaves them behind (one real pak carried
    /// its whole .git folder, 384 files). Readmes and images are kept: the Library shows those.
    /// </summary>
    public static bool IsBuildLeftover(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        var extension = Path.GetExtension(path);
        return path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).StartsWith(".git", StringComparison.OrdinalIgnoreCase)
            || extension is ".np++" or ".cmd" or ".bat" or ".ps1" or ".sh" or ".sln" or ".csproj" or ".user" or ".suo" or ".bak" or ".tmp" or ".log";
    }

    /// <summary>Readmes and pictures authors put in on purpose — not worth a warning.</summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    /// <summary>Not game content: anything that isn't an Unreal asset or a data table, except a pak-root .EXMOD (classic IMM bakes its source in on purpose) and readmes/pictures.</summary>
    private static bool IsUnusedByTheGame(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if ((extension.Equals(".EXMOD", StringComparison.OrdinalIgnoreCase) && !relativePath.Contains('/')) || DocumentExtensions.Contains(extension))
        {
            return false;
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            // The game reads JSON data tables from data/ only (a blueprint dumped to .json elsewhere does nothing).
            return !relativePath.StartsWith("data/", StringComparison.OrdinalIgnoreCase);
        }

        return !GameFileExtensions.Contains(extension);
    }

    private static void InspectTables(string root, IReadOnlyList<string> files, string dataFolder, string pakName, MergeReport report)
    {
        if (!Directory.Exists(dataFolder))
        {
            return;
        }

        var unchanged = new List<string>();
        var olderThanGame = new List<string>();
        foreach (var file in files.Where(f => f.StartsWith("data/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var currentFile = file["data/".Length..].Replace('/', '-');
            var baseRows = BaseDataFileReader.ReadKeyedTable(dataFolder, currentFile, report: null);
            if (baseRows is null)
            {
                continue;
            }

            JsonObject modRows;
            try
            {
                var parsed = DuplicateTolerantJson.Parse(File.ReadAllText(Path.Combine(root, file)))!.AsObject();
                modRows = DataTableJson.RowsToKeyedObject(parsed, _ => { });
            }
            catch (Exception)
            {
                continue; // unreadable JSON is reported by the conversion itself
            }

            var table = Path.GetFileNameWithoutExtension(file);
            var missing = baseRows.Select(kv => kv.Key).Where(name => !modRows.ContainsKey(name)).ToList();
            if (missing.Count > 0)
            {
                // A pak replaces the whole table, so rows it lacks vanish from the game when it is used
                // on its own. Starlink's merge applies only the pak's real changes, so here it's harmless.
                olderThanGame.Add($"{table} lacks {missing.Count} row(s) the game has now (e.g. {missing[0]})");
            }
            else if (modRows.Count == baseRows.Count && modRows.All(kv => baseRows.TryGetPropertyValue(kv.Key, out var b) && JsonNode.DeepEquals(kv.Value, b)))
            {
                unchanged.Add(table);
            }
        }

        if (olderThanGame.Count > 0)
        {
            report.AddWarning(
                $"'{pakName}' looks built for an older game version: {string.Join("; ", olderThanGame.Take(3))}{(olderThanGame.Count > 3 ? $"; and {olderThanGame.Count - 3} more table(s)" : "")}. "
                + "Used on its own in the game it would remove those; merged by Starlink only its differences apply — and a few of those may be the old game's values rather than real changes. Check Nexus for an update.");
        }

        if (unchanged.Count > 0)
        {
            report.AddWarning(
                $"'{pakName}' ships {unchanged.Count} table(s) it doesn't change at all ({string.Join(", ", unchanged.Take(3))}{(unchanged.Count > 3 ? ", …" : "")}). "
                + "Harmless in Starlink; on its own in the game they would undo other mods' changes to those tables.");
        }
    }
}
