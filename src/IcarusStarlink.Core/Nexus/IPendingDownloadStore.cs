namespace IcarusStarlink.Core.Nexus;

/// <summary>Add/Remove persist immediately, matching INexusWatchlistStore's own convention — no batch-edit workflow to defer for.</summary>
public interface IPendingDownloadStore
{
    IReadOnlyList<PendingDownloadEntry> Entries { get; }

    void Add(PendingDownloadEntry entry);

    /// <summary>Removes the tracked entry only — does not delete the downloaded file itself; callers (Activate/Discard) decide that separately.</summary>
    void Remove(int modId, int fileId);
}
