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
    /// <param name="keepArcFissionDataPak">
    /// True (the default) leaves ArcFission Library's own ZZ_ArcFission_Data_&lt;n&gt;_P.pak in the
    /// mods folder; false backs it up and removes it like any other file there — for a pak left
    /// behind after the library itself was uninstalled or turned off.
    /// </param>
    /// <param name="backUpWhatsThere">
    /// True (the default, every normal install) backs up the merged pak and its mod list already in
    /// the game. False only for a Find the broken mod round after the first one (and putting the
    /// mods back): what's there is a temporary round pak, and backing it up would push the
    /// player's real install (backed up by the first round) out of Restore previous install. Other
    /// files in the mods folder are backed up either way.
    /// </param>
    Task<InstallResult> InstallAsync(
        string stagedPakPath, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default, bool keepArcFissionDataPak = true, bool backUpWhatsThere = true);

    /// <summary>The newest backup of the installed merged pak and its mod list, as Restore previous install would put back; null when there's none.</summary>
    Task<PreviousInstall?> FindPreviousInstallAsync(string backupDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the newest merged pak backup (and the ISL-Merged.txt backup made with it) back into
    /// Content\Paks\mods, after backing up what's installed now, so it can be undone the same way.
    /// Returns what was restored, or null when there's no backup to restore.
    /// </summary>
    Task<PreviousInstall?> RestorePreviousInstallAsync(
        string stagedPakFileName, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Reads this app's own manifest back from the real game folder — mod names from the pak's ISL-Merged.txt — for the "installed vs this list" comparison. Empty (never throws) if the manifest, or the game folder itself, doesn't exist yet.</summary>
    Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the real installed pak (and its sibling manifest, if present) from
    /// Content\Paks\mods — a standalone uninstall, independent of Install. Backs up whatever it
    /// removes first, the same keep-last-5 rotation InstallAsync already uses, so it's always
    /// undoable. stagedPakFileName only supplies the target's own filename (this app's own output
    /// pak is always the same name across every Rebuild; the merged pak's earlier names,
    /// InstallManifestNames.LegacyMergedPakFileNames, are removed too) — no staged file needs to exist on disk
    /// for this to run, unlike InstallAsync. Returns false (not an error) if nothing this app
    /// recognizes was installed there to begin with. backUpWhatsThere false: as InstallAsync's
    /// (a later Find the broken mod round removes a round pak without backing it up).
    /// </summary>
    Task<bool> RemoveInstalledPakAsync(
        string stagedPakFileName, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default, bool backUpWhatsThere = true);
}
