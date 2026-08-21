namespace IcarusStarlink.PakIO.Install;

public interface IInstallService
{
    /// <summary>
    /// Copies an already-staged pak (and its sibling manifest, if present) into
    /// icarusContentPath\Paks\mods — the real folder Icarus itself scans for mod paks — and syncs
    /// stagedUe4ssModFolderPaths (each an absolute path to one staged UE4SS mod's own folder) into
    /// icarusContentPath's sibling Binaries\Win64\ue4ss\Mods (see Ue4ssGamePaths). "Syncs" means a
    /// mod this app installed on a previous call that's no longer in the current set is backed up
    /// and removed too — not left behind — while anything it didn't install is never touched.
    /// Backs up whatever already sits at each destination first (keep-last-5 rotation in
    /// backupDirectory), so an install is always undoable. Also serves as the spec's own "Copy
    /// built pack to game" retry action — re-running this against the same staged pak (e.g. after
    /// a locked-file failure) just re-copies it, no rebuild needed. Throws if stagedPakPath
    /// doesn't exist (nothing staged — Rebuild hasn't run yet) or a copy itself fails.
    /// </summary>
    Task<InstallResult> InstallAsync(
        string stagedPakPath, string? stagedManifestPath, IReadOnlyList<string> stagedUe4ssModFolderPaths,
        string icarusContentPath, string backupDirectory, CancellationToken cancellationToken = default);

    /// <summary>Removes only the UE4SS mod folders this app itself installed (tracked via its own manifest, written during InstallAsync) — never the built-in UE4SS framework mods or anything installed by hand or another tool. A no-op if nothing this app installed is still present.</summary>
    Task RemoveInstalledUe4ssModsAsync(string icarusContentPath, CancellationToken cancellationToken = default);
}
