using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Conflict picks ("file|row|field" → winner) as they travel in a patch. A winner is a mod's folder
/// name, a pak mod's "folder/pakname", or one of the built-in ids (gameplay options, ArcFission).
/// Folder names aren't shared between machines, so the folder part is what changes hands.
/// </summary>
public static class PatchWinners
{
    /// <summary>The picks whose winner is one of the exported folders (or a built-in id); the rest (a mod left out, a pick still saved by name) can't be matched on another machine.</summary>
    public static Dictionary<string, string> ForExport(IReadOnlyDictionary<string, string> winners, IReadOnlyCollection<string> exportedFolders)
    {
        var folders = new HashSet<string>(exportedFolders, StringComparer.OrdinalIgnoreCase);
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in winners)
        {
            if (IsBuiltIn(winner) || folders.Contains(FolderOf(winner)))
            {
                kept[key] = winner;
            }
        }

        return kept;
    }

    /// <summary>
    /// The patch's picks with each exporter folder rewritten to the local folder it matched
    /// (<paramref name="localFolderByPatchFolder"/>); a pick whose mod didn't match anything here is dropped.
    /// </summary>
    public static Dictionary<string, string> Remap(IReadOnlyDictionary<string, string>? patchWinners, IReadOnlyDictionary<string, string> localFolderByPatchFolder)
    {
        var remapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in patchWinners ?? new Dictionary<string, string>())
        {
            if (IsBuiltIn(winner))
            {
                remapped[key] = winner;
                continue;
            }

            var folder = FolderOf(winner);
            if (localFolderByPatchFolder.TryGetValue(folder, out var local))
            {
                remapped[key] = local + winner[folder.Length..];
            }
        }

        return remapped;
    }

    /// <summary>The picks whose winner is still in the queue (by folder, or by the display name a pick may still be saved under), or a built-in id.</summary>
    public static Dictionary<string, string> StillQueued(IReadOnlyDictionary<string, string> winners, IReadOnlyCollection<string> queuedFolders, IReadOnlyCollection<string> queuedNames)
    {
        var folders = new HashSet<string>(queuedFolders, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(queuedNames, StringComparer.Ordinal);
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in winners)
        {
            if (IsBuiltIn(winner) || folders.Contains(FolderOf(winner)) || names.Contains(winner))
            {
                kept[key] = winner;
            }
        }

        return kept;
    }

    private static bool IsBuiltIn(string winner) =>
        winner is MergeInstallViewModel.OptionsWinnerId or MergeInstallViewModel.ArcFissionWinnerId;

    private static string FolderOf(string winner)
    {
        var slash = winner.IndexOf('/');
        return slash > 0 ? winner[..slash] : winner;
    }
}
