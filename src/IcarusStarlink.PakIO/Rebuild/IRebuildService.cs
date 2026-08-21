using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Rebuild;

public interface IRebuildService
{
    /// <summary>
    /// Merges queuedMods (index 0 = lowest priority, same convention as MergeEngine.Merge —
    /// later entries win conflicts by default) against the real base game data in dataFolder
    /// (Phase 5's "Update data folder" output), then applies gameplayOptions as a final pass over
    /// the merge result (matching classic IMM's own documented behavior — these apply after the
    /// queue, not as another queued mod, and work even with an empty queue), then packs the result
    /// into one pak at outputPakPath plus a sibling manifest, matching a real installed merged
    /// pack's own file layout. Throws on failure (missing/unreadable base data, UnrealPak itself
    /// failing) — same convention as IUnrealPakService.
    /// </summary>
    Task<RebuildResult> RebuildAsync(
        IReadOnlyList<ExmodPackageContents> queuedMods,
        GameplayOptions gameplayOptions,
        string dataFolder,
        string unrealPakExePath,
        string outputPakPath,
        CancellationToken cancellationToken = default);
}
