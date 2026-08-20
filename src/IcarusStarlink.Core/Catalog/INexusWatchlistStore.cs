namespace IcarusStarlink.Core.Catalog;

/// <summary>Add/Remove persist immediately (matching ILibraryRepository's mutating methods), not a separate explicit Save() — there's no batch-edit workflow here to defer for.</summary>
public interface INexusWatchlistStore
{
    IReadOnlyList<NexusWatchlistEntry> Entries { get; }

    void Add(NexusWatchlistEntry entry);

    void Remove(int nexusId);

    void UpdateName(int nexusId, string name);
}
