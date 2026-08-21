namespace IcarusStarlink.PakIO.Install;

public interface IInstallService
{
    /// <summary>
    /// Copies an already-staged pak (and its sibling manifest, if present) into
    /// icarusContentPath\Paks\mods — the real folder Icarus itself scans for mod paks. Backs up
    /// whatever pak already sits at the destination first (keep-last-5 rotation in
    /// backupDirectory), so an install is always undoable. Also serves as the spec's own "Copy
    /// built pack to game" retry action — re-running this against the same staged pak (e.g. after
    /// a locked-file failure) just re-copies it, no rebuild needed. Throws if stagedPakPath
    /// doesn't exist (nothing staged — Rebuild hasn't run yet) or the copy itself fails.
    /// </summary>
    Task<InstallResult> InstallAsync(
        string stagedPakPath, string? stagedManifestPath, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default);
}
