namespace IcarusStarlink.Core.Nexus;

/// <summary>Add/Remove persist immediately, matching INexusWatchlistStore's own convention — no batch-edit workflow to defer for.</summary>
public interface IPendingDownloadStore
{
    IReadOnlyList<PendingDownloadEntry> Entries { get; }

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

    /// <summary>Records every item an activation produced, without removing the entry (the first item becomes the legacy primary pair; none resets it to never-activated), plus the README requirements found alongside.</summary>
    void SetActivation(int modId, int fileId, IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<string> requirements);
}
