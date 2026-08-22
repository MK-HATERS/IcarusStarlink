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
        string stagedPakPath, string? stagedManifestPath, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default)
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

        if (stagedManifestPath is not null && File.Exists(stagedManifestPath))
        {
            var targetManifestPath = Path.Combine(modsDirectory, Path.GetFileName(stagedManifestPath));
            File.Copy(stagedManifestPath, targetManifestPath, overwrite: true);
        }

        return Task.FromResult(new InstallResult(targetPakPath, backupPakPath));
    }

    public Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default)
    {
        var pakManifestPath = Path.Combine(icarusContentPath, "Paks", "mods", InstallManifestNames.PakManifest);
        var modNames = File.Exists(pakManifestPath)
            // First line is the "Includes the following mods:" header, not a mod name.
            ? File.ReadAllLines(pakManifestPath).Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
            : [];

        return Task.FromResult(new InstalledState(modNames));
    }
}
