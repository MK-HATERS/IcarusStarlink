namespace IcarusStarlink.PakIO.Install;

public interface IInstallService
{
    /// <summary>
    /// Copies an already-staged pak (and its sibling manifest, if present — always the same
    /// deterministic path next to the pak that RebuildService's own WriteManifest writes, derived
    /// here rather than taken from the caller, so a caller can't accidentally pass a stale/missing
    /// path from an earlier Rebuild) into icarusContentPath\Paks\mods — the real folder Icarus
    /// itself scans for mod paks. Backs up whatever already sits there first (keep-last-5 rotation
    /// in backupDirectory), so an install is always undoable. Also serves as the spec's own "Copy
    /// built pack to game" retry action — re-running this against the same staged pak (e.g. after
    /// a locked-file failure) just re-copies it, no rebuild needed. Throws if stagedPakPath
    /// doesn't exist (nothing staged — Rebuild hasn't run yet) or a copy itself fails. Attached
    /// opaque/prebuilt paks (LibraryEntry.IsOpaquePak) are already folded into this one staged pak
    /// by RebuildService itself, not installed separately — see its own doc comment. UE4SS mods are
    /// a separate concern — see IUe4ssModStateService.
    /// </summary>
    Task<InstallResult> InstallAsync(
        string stagedPakPath, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Reads this app's own manifest back from the real game folder — mod names from the pak's ISL-Merged.txt — for the "installed vs this list" comparison. Empty (never throws) if the manifest, or the game folder itself, doesn't exist yet.</summary>
    Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the real installed pak (and its sibling manifest, if present) from
    /// Content\Paks\mods — a standalone uninstall, independent of Install. Backs up whatever it
    /// removes first, the same keep-last-5 rotation InstallAsync already uses, so it's always
    /// undoable. stagedPakFileName only supplies the target's own filename (this app's own output
    /// pak is always the same name across every Rebuild) — no staged file needs to exist on disk
    /// for this to run, unlike InstallAsync. Returns false (not an error) if nothing this app
    /// recognizes was installed there to begin with.
    /// </summary>
    Task<bool> RemoveInstalledPakAsync(
        string stagedPakFileName, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default);
}
