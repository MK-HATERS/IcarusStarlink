namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Copies a staged pak into the real game's Content\Paks\mods — the one thing this app ever writes
/// there. Backup scope is deliberately narrow: only the one file this service is about to
/// overwrite, not the whole folder. UE4SS mods are a separate concern (IUe4ssModStateService,
/// Phase 8.5) — this service no longer touches the UE4SS Mods folder at all.
/// </summary>
public sealed class InstallService : IInstallService
{
    public Task<InstallResult> InstallAsync(
        string stagedPakPath, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!File.Exists(stagedPakPath))
        {
            throw new FileNotFoundException($"No staged pak at '{stagedPakPath}' — click Rebuild first.");
        }

        var modsDirectory = Path.Combine(icarusContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);

        var targetPakPath = Path.Combine(modsDirectory, Path.GetFileName(stagedPakPath));
        var backupPakPath = FolderBackup.BackupFile(targetPakPath, backupDirectory);

        File.Copy(stagedPakPath, targetPakPath, overwrite: true);

        // Same deterministic sibling path RebuildService.WriteManifest always writes to — derived
        // here rather than cached by the caller, so a manifest from an earlier Rebuild (this
        // session or a previous one) is never silently missed just because the in-memory path
        // wasn't carried forward (e.g. after an app restart).
        var stagedManifestPath = Path.Combine(Path.GetDirectoryName(stagedPakPath)!, InstallManifestNames.PakManifest);
        if (File.Exists(stagedManifestPath))
        {
            var targetManifestPath = Path.Combine(modsDirectory, InstallManifestNames.PakManifest);
            // Backed up too, same as the pak itself — without this, installing a pak built from a
            // smaller queue than what's currently listed silently loses the previous manifest (and
            // its "installed mods" record GetInstalledStateAsync reads below) with no way to
            // recover what was previously installed.
            FolderBackup.BackupFile(targetManifestPath, backupDirectory);
            File.Copy(stagedManifestPath, targetManifestPath, overwrite: true);
        }

        return new InstallResult(targetPakPath, backupPakPath);
    }, cancellationToken);

    public Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var pakManifestPath = Path.Combine(icarusContentPath, "Paks", "mods", InstallManifestNames.PakManifest);
        var modNames = File.Exists(pakManifestPath)
            // First line is the "Includes the following mods:" header, not a mod name.
            ? File.ReadAllLines(pakManifestPath).Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
            : [];

        return new InstalledState(modNames);
    }, cancellationToken);
}
