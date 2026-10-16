namespace IcarusStarlink.Catalog.Nexus;

/// <summary>One entry of a mod's "file_updates" list: the author uploaded <see cref="NewFileId"/> as the new version of <see cref="OldFileId"/>.</summary>
public sealed record NexusFileUpdate(int OldFileId, int NewFileId);

/// <summary>A mod's files plus Nexus's own record of which file replaced which.</summary>
public sealed record NexusModFileSet(IReadOnlyList<NexusModFile> Files, IReadOnlyList<NexusFileUpdate> Updates);

/// <summary>
/// Follows one file through its updates. A Nexus mod can offer several files side by side —
/// Deyvid's All-in-One (mod 317) has five variants with their own versions (1.0.8, and 1.0.5 for
/// one of them) — so "is there an update" has to be asked of the file the user actually has, not
/// of the mod's single headline version. When an author uploads a new version of a file, Nexus
/// gives it a new file ID and records old → new in file_updates; walking that chain from the
/// installed file ID lands on its newest version.
/// </summary>
public static class NexusFileLineage
{
    /// <summary>The newest file descended from <paramref name="fileId"/> (the file itself when nothing replaced it), or null when that file isn't listed at all (removed from the mod).</summary>
    public static NexusModFile? Newest(NexusModFileSet set, int fileId)
    {
        var next = new Dictionary<int, int>();
        foreach (var update in set.Updates)
        {
            // Several updates from one old file shouldn't happen; if it does, the latest one listed wins.
            next[update.OldFileId] = update.NewFileId;
        }

        var path = new List<int> { fileId };
        var seen = new HashSet<int> { fileId };
        while (next.TryGetValue(path[^1], out var newer) && seen.Add(newer))
        {
            path.Add(newer);
        }

        // The chain can end at a file the author has since deleted: step back to the newest one that still exists.
        var byId = new Dictionary<int, NexusModFile>();
        foreach (var file in set.Files)
        {
            byId.TryAdd(file.FileId, file);
        }

        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (byId.TryGetValue(path[i], out var file))
            {
                return file;
            }
        }

        return null;
    }
}
