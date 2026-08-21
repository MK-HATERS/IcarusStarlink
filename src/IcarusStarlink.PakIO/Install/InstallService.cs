namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Copies a staged pak into the real game's Content\Paks\mods — Icarus (like most Unreal Engine
/// games) scans that folder for mod paks at launch, matching the real folder confirmed against
/// the user's own currently-installed classic-IMM merged pak during Phase 6.1's research.
/// Backup scope is deliberately narrow: only the file this service itself is about to overwrite,
/// not the whole mods folder — this app never writes anything else there, so that's the entire
/// blast radius a backup needs to cover.
/// </summary>
public sealed class InstallService : IInstallService
{
    private const int MaxBackups = 5;

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
        var backupPakPath = BackupExisting(targetPakPath, backupDirectory);

        File.Copy(stagedPakPath, targetPakPath, overwrite: true);

        if (stagedManifestPath is not null && File.Exists(stagedManifestPath))
        {
            var targetManifestPath = Path.Combine(modsDirectory, Path.GetFileName(stagedManifestPath));
            File.Copy(stagedManifestPath, targetManifestPath, overwrite: true);
        }

        return Task.FromResult(new InstallResult(targetPakPath, backupPakPath));
    }

    private static string? BackupExisting(string targetPakPath, string backupDirectory)
    {
        if (!File.Exists(targetPakPath))
        {
            return null;
        }

        Directory.CreateDirectory(backupDirectory);

        var baseName = Path.GetFileNameWithoutExtension(targetPakPath);
        var extension = Path.GetExtension(targetPakPath);
        var backupPath = Path.Combine(backupDirectory, $"{baseName}_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{extension}");
        File.Copy(targetPakPath, backupPath);

        PruneOldBackups(backupDirectory, baseName, extension);
        return backupPath;
    }

    private static void PruneOldBackups(string backupDirectory, string baseName, string extension)
    {
        var backups = Directory.GetFiles(backupDirectory, $"{baseName}_*{extension}")
            .OrderByDescending(File.GetCreationTimeUtc)
            .ToList();

        foreach (var stale in backups.Skip(MaxBackups))
        {
            File.Delete(stale);
        }
    }
}
