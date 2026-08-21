using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Rebuild;

public interface IRebuildService
{
    /// <summary>
    /// Merges queuedMods (index 0 = lowest priority, same convention as MergeEngine.Merge —
    /// later entries win conflicts by default) against the real base game data in dataFolder
    /// (Phase 5's "Update data folder" output), then packs the result into one pak at
    /// outputPakPath plus a sibling manifest, matching a real installed merged pack's own file
    /// layout. Throws on failure (missing/unreadable base data, UnrealPak itself failing) — same
    /// convention as IUnrealPakService.
    /// </summary>
    Task<RebuildResult> RebuildAsync(
        IReadOnlyList<ExmodPackageContents> queuedMods,
        string dataFolder,
        string unrealPakExePath,
        string outputPakPath,
        CancellationToken cancellationToken = default);
}
