namespace IcarusStarlink.PakIO.Install;

public interface IInstallService
{
    /// <summary>
    /// Copies an already-staged pak (and its sibling manifest, if present — always the same
    /// deterministic path next to the pak that RebuildService's own WriteManifest writes, derived
    /// here rather than taken from the caller, so a caller can't accidentally pass a stale/missing
    /// path from an earlier Rebuild) into icarusContentPath\Paks\mods — the real folder Icarus
    /// itself scans for mod paks. Backs up what's already there first, into backupDirectory (the
    /// merged pak and its list keep the newest Settings › Maintenance › Backups to keep › Merged
    /// pak installs, 3 by default; other files keep 5), so an install can be undone. Also what the
    /// Merge page's Copy built pack to game runs: re-running this against the same staged pak
    /// (e.g. after a locked-file failure) just re-copies it, no rebuild needed. Throws if stagedPakPath
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

    /// <summary>
    /// Every kept backup of the merged pak, newest first, each with its mod list and the mod names
    /// in it (BackupCleanup.ListMergedPakBackups). Empty when there's none. Given the game folder,
    /// a backup with the same bytes as the merged pak installed there is marked IsInGame (only a
    /// backup of the same size is read).
    /// </summary>
    Task<IReadOnlyList<PreviousInstall>> ListPreviousInstallsAsync(
        string backupDirectory, string? icarusContentPath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts back the merged pak backup the player chose from ListPreviousInstallsAsync (newest or
    /// not) and the ISL-Merged.txt backup made with it, into Content\Paks\mods. What's installed
    /// now is backed up first, so it can be undone the same way, and the chosen backup is kept by
    /// the prune after, whatever its age. Returns what was restored, its ManifestBackupPath null
    /// when that list is gone; null (nothing changed) when the chosen pak backup is no longer
    /// there or isn't in backupDirectory. The installed name is worked out from the game folder
    /// (InstallManifestNames.InstalledMergedPakFileName).
    /// </summary>
    Task<PreviousInstall?> RestoreInstallAsync(
        string icarusContentPath, string backupDirectory, PreviousInstall chosen,
        CancellationToken cancellationToken = default);

    /// <summary>Reads this app's own manifest back from the real game folder — mod names from the pak's ISL-Merged.txt — for the "installed vs this list" comparison. Empty (never throws) if the manifest, or the game folder itself, doesn't exist yet.</summary>
    Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the real installed pak (and its sibling manifest, if present) from
    /// Content\Paks\mods — a standalone uninstall, independent of Install. Backs up whatever it
    /// removes first, the same backups InstallAsync makes, so it can be undone. The installed name
    /// is worked out from the game folder (InstallManifestNames.InstalledMergedPakFileName), and
    /// the merged pak under every name it has had (AllMergedPakFileNames) is removed too. No
    /// staged file is needed, unlike InstallAsync. Returns false (not an error) if nothing this
    /// app recognizes was installed there to begin with. backUpWhatsThere false: as InstallAsync's
    /// (a later Find the broken mod round removes a round pak without backing it up).
    /// </summary>
    Task<bool> RemoveInstalledPakAsync(
        string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default, bool backUpWhatsThere = true);
}
