namespace IcarusStarlink.Core.Nexus;

/// <summary>Add/Remove persist immediately, matching INexusWatchlistStore's own convention — no batch-edit workflow to defer for.</summary>
public interface IPendingDownloadStore
{
    IReadOnlyList<PendingDownloadEntry> Entries { get; }

    /// <summary>
    /// The list file existed but couldn't be read this session, so Entries may be missing
    /// downloads that are still on disk. Stays true until Starlink restarts, even after a save.
    /// </summary>
    bool HasLoadProblem => false;

    void Add(PendingDownloadEntry entry);

    /// <summary>Removes the tracked entry only — does not delete the downloaded file itself; callers (Discard) decide that separately. Activate no longer removes the entry (see PendingDownloadEntry's own doc comment) — SetActivation is what it calls instead.</summary>
    void Remove(int modId, int fileId);

    /// <summary>Removes several entries with one save (Discard selected). The default removes them one at a time.</summary>
    void RemoveMany(IReadOnlyCollection<(int ModId, int FileId)> downloads)
    {
        foreach (var (modId, fileId) in downloads)
        {
            Remove(modId, fileId);
        }
    }

    /// <summary>Records the Nexus file's version on a download. The default sets it in memory only.</summary>
    void SetFileVersion(int modId, int fileId, string fileVersion)
    {
        if (Entries.FirstOrDefault(e => e.ModId == modId && e.FileId == fileId) is { } entry)
        {
            entry.FileVersion = fileVersion;
        }
    }

    /// <summary>Records several downloads' versions with one save. The default sets them one at a time.</summary>
    void SetFileVersions(IReadOnlyCollection<(int ModId, int FileId, string FileVersion)> versions)
    {
        foreach (var (modId, fileId, fileVersion) in versions)
        {
            SetFileVersion(modId, fileId, fileVersion);
        }
    }

    /// <summary>Records every item an activation produced, without removing the entry (the first item becomes the legacy primary pair; none resets it to never-activated), plus the README requirements found alongside.</summary>
    void SetActivation(int modId, int fileId, IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<string> requirements);
}
