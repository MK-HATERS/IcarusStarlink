namespace IcarusStarlink.Core.Nexus;

/// <summary>Add/Remove persist immediately, matching INexusWatchlistStore's own convention — no batch-edit workflow to defer for.</summary>
public interface IPendingDownloadStore
{
    IReadOnlyList<PendingDownloadEntry> Entries { get; }

    void Add(PendingDownloadEntry entry);

    /// <summary>Removes the tracked entry only — does not delete the downloaded file itself; callers (Discard) decide that separately. Activate no longer removes the entry (see PendingDownloadEntry's own doc comment) — SetActivation is what it calls instead.</summary>
    void Remove(int modId, int fileId);

    /// <summary>Records what a successful Activate produced, without removing the entry — folderName/kind null resets it back to never-activated.</summary>
    void SetActivation(int modId, int fileId, string? folderName, PendingDownloadActivationKind? kind);
}
