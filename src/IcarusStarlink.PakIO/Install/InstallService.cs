using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Copies a staged pak into the real game's Content\Paks\mods, and the current profile's staged
/// UE4SS mods into Binaries\Win64\ue4ss\Mods (see Ue4ssGamePaths) — Icarus scans both at launch.
/// Backup scope is deliberately narrow: only what this service itself is about to overwrite, not
/// either folder wholesale — this app never writes anything else there, so that's the entire blast
/// radius a backup needs to cover.
/// </summary>
public sealed class InstallService : IInstallService
{
    private const int MaxBackups = 5;
    private const string InstalledModsManifestFileName = "ISL-Installed-Mods.txt";

    public Task<InstallResult> InstallAsync(
        string stagedPakPath, string? stagedManifestPath, IReadOnlyList<string> stagedUe4ssModFolderPaths,
        string icarusContentPath, string backupDirectory, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(stagedPakPath))
        {
            throw new FileNotFoundException($"No staged pak at '{stagedPakPath}' — click Rebuild first.");
        }

        var modsDirectory = Path.Combine(icarusContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);

        var targetPakPath = Path.Combine(modsDirectory, Path.GetFileName(stagedPakPath));
        var backupPakPath = BackupExistingFile(targetPakPath, backupDirectory);

        File.Copy(stagedPakPath, targetPakPath, overwrite: true);

        if (stagedManifestPath is not null && File.Exists(stagedManifestPath))
        {
            var targetManifestPath = Path.Combine(modsDirectory, Path.GetFileName(stagedManifestPath));
            File.Copy(stagedManifestPath, targetManifestPath, overwrite: true);
        }

        var gameModsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);
        var installedUe4ssModNames = SyncUe4ssMods(stagedUe4ssModFolderPaths, gameModsFolder, backupDirectory);

        return Task.FromResult(new InstallResult(targetPakPath, backupPakPath, installedUe4ssModNames));
    }

    /// <summary>Removes only the UE4SS mod folders this app itself installed there (tracked via the manifest InstallAsync writes) — never the built-in UE4SS framework mods or anything installed by hand or another tool, which this app has no way to positively identify as safe to touch.</summary>
    public Task RemoveInstalledUe4ssModsAsync(string icarusContentPath, CancellationToken cancellationToken = default)
    {
        var gameModsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);
        var manifestPath = Path.Combine(gameModsFolder, InstalledModsManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return Task.CompletedTask;
        }

        foreach (var name in File.ReadAllLines(manifestPath))
        {
            var folder = Path.Combine(gameModsFolder, name);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        File.Delete(manifestPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reconciles the real Mods folder against stagedUe4ssModFolderPaths — a real sync, not just an
    /// add: a mod this app installed on a previous Install that's no longer in the current set gets
    /// backed up and removed too, so switching a profile's UE4SS set doesn't leave stale mods
    /// behind forever. Only ever touches folders this app's own manifest says it put there, or is
    /// about to put there — never a folder it doesn't recognize.
    /// </summary>
    private static IReadOnlyList<string> SyncUe4ssMods(IReadOnlyList<string> stagedModFolderPaths, string gameModsFolder, string backupDirectory)
    {
        Directory.CreateDirectory(gameModsFolder);
        var manifestPath = Path.Combine(gameModsFolder, InstalledModsManifestFileName);
        var previouslyInstalled = File.Exists(manifestPath) ? File.ReadAllLines(manifestPath) : [];
        var ue4ssBackupDirectory = Path.Combine(backupDirectory, "UE4SS");

        var desiredNames = stagedModFolderPaths.Select(GetFolderName).ToList();

        foreach (var stale in previouslyInstalled.Except(desiredNames, StringComparer.OrdinalIgnoreCase))
        {
            var staleFolder = Path.Combine(gameModsFolder, stale);
            if (Directory.Exists(staleFolder))
            {
                BackupFolder(staleFolder, ue4ssBackupDirectory);
                Directory.Delete(staleFolder, recursive: true);
            }
        }

        foreach (var stagedPath in stagedModFolderPaths)
        {
            var targetFolder = Path.Combine(gameModsFolder, GetFolderName(stagedPath));
            if (Directory.Exists(targetFolder))
            {
                BackupFolder(targetFolder, ue4ssBackupDirectory);
                Directory.Delete(targetFolder, recursive: true);
            }

            CopyDirectory(stagedPath, targetFolder);
        }

        File.WriteAllLines(manifestPath, desiredNames);
        return desiredNames;
    }

    private static string GetFolderName(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }
    }

    private static void BackupFolder(string sourceFolder, string backupBaseDirectory)
    {
        Directory.CreateDirectory(backupBaseDirectory);
        var modName = Path.GetFileName(sourceFolder);
        var backupPath = Path.Combine(backupBaseDirectory, $"{modName}_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        CopyDirectory(sourceFolder, backupPath);
        PruneOldFolderBackups(backupBaseDirectory, modName);
    }

    private static void PruneOldFolderBackups(string backupDirectory, string modName)
    {
        var backups = Directory.GetDirectories(backupDirectory, $"{modName}_*")
            .OrderByDescending(Directory.GetCreationTimeUtc)
            .ToList();

        foreach (var stale in backups.Skip(MaxBackups))
        {
            Directory.Delete(stale, recursive: true);
        }
    }

    private static string? BackupExistingFile(string targetPakPath, string backupDirectory)
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

        PruneOldFileBackups(backupDirectory, baseName, extension);
        return backupPath;
    }

    private static void PruneOldFileBackups(string backupDirectory, string baseName, string extension)
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
