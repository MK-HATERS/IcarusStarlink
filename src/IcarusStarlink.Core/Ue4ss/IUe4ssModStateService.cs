namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Phase 8.5 rework: UE4SS mods are managed directly (enable/disable), independent of Profiles or
/// the pak Install pipeline — a mod is either "enabled" (its folder is in the real game Mods
/// folder) or "disabled" (its folder is only in this app's own staging). This is the single source
/// of truth Library's UE4SS tab reads and writes through.
/// </summary>
public interface IUe4ssModStateService
{
    /// <summary>Every known mod — the union of what's really installed in the game and what's staged in this app, deduplicated by name — with its current real enabled state.</summary>
    IReadOnlyList<Ue4ssModState> GetAll(string gameModsFolderPath);

    /// <summary>
    /// Moves each named mod to match its desired state — enabling moves it staging→game, disabling
    /// moves it game→staging (backed up first, keep-last-5) — but only touches a name whose desired
    /// state actually differs from its current one. Names not present in desiredEnabledByName are
    /// left alone entirely. Never throws for a single mod's own failure (a locked file mid-copy, an
    /// empty/missing staged folder with nothing to actually enable) — every OTHER named mod in the
    /// same call still gets attempted, and any that failed are returned rather than silently
    /// aborting the rest of the batch with no way to tell which ones did or didn't go through.
    /// </summary>
    IReadOnlyList<Ue4ssModApplyFailure> Apply(string gameModsFolderPath, IReadOnlyDictionary<string, bool> desiredEnabledByName, string backupDirectory);
}
