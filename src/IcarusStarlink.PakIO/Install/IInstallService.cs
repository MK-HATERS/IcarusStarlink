namespace IcarusStarlink.PakIO.Install;

public interface IInstallService
{
    /// <summary>
    /// Copies an already-staged pak (and its sibling manifest, if present) into
    /// icarusContentPath\Paks\mods — the real folder Icarus itself scans for mod paks. Backs up
    /// whatever already sits there first (keep-last-5 rotation in backupDirectory), so an install
    /// is always undoable. Also serves as the spec's own "Copy built pack to game" retry action —
    /// re-running this against the same staged pak (e.g. after a locked-file failure) just
    /// re-copies it, no rebuild needed. Throws if stagedPakPath doesn't exist (nothing staged —
    /// Rebuild hasn't run yet) or a copy itself fails. UE4SS mods are a separate concern — see
    /// IUe4ssModStateService.
    /// </summary>
    Task<InstallResult> InstallAsync(
        string stagedPakPath, string? stagedManifestPath, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Reads this app's own manifest back from the real game folder — mod names from the pak's ISL-Merged.txt — for the "installed vs this list" comparison. Empty (never throws) if the manifest, or the game folder itself, doesn't exist yet.</summary>
    Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default);
}
