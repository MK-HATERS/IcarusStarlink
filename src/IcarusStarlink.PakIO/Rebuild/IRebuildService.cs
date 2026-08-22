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
    /// <param name="manualPicks">
    /// The advanced conflict picker's own overrides, passed straight through to MergeEngine.Merge —
    /// null (the default) means every conflict resolves via the registry's own automatic rule
    /// (last-write-wins for a plain scalar). Only valid against the exact same queuedMods this was
    /// computed against (see MergeEngine.FindConflicts) — a stale pick against a since-changed queue
    /// throws ArgumentOutOfRangeException rather than silently applying the wrong mod's value.
    /// </param>
    Task<RebuildResult> RebuildAsync(
        IReadOnlyList<ExmodPackageContents> queuedMods,
        GameplayOptions gameplayOptions,
        string dataFolder,
        string unrealPakExePath,
        string outputPakPath,
        IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? manualPicks = null,
        CancellationToken cancellationToken = default);
}
