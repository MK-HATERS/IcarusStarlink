using System.IO.Compression;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Installs/updates the UE4SS loader itself (dwmapi.dll + Binaries\Win64\ue4ss\UE4SS.dll) — a
/// deliberate reversal of this app's own earlier stated boundary (6.5: "never installs/manages the
/// loader itself, only mod folders under Mods\"), so it carries extra caution: a full backup of the
/// whole ue4ss\ folder + dwmapi.dll before any write, and every field the zip's own Mods\ folder or
/// UE4SS-settings.ini would otherwise touch is applied additively (never overwrites something
/// already there), so an update can't clobber a user's own installed mods or customized settings —
/// only UE4SS.dll/dwmapi.dll themselves (the actual thing being updated) and informational files
/// (README/Changelog/LICENSE) are unconditionally overwritten.
/// </summary>
public sealed class Ue4ssLoaderInstallService : IUe4ssLoaderInstallService
{
    public Ue4ssLoaderStatus GetStatus(string icarusContentPath)
    {
        var dllPath = Ue4ssGamePaths.ResolveLoaderDllPath(icarusContentPath);
        var dwmapiPath = Ue4ssGamePaths.ResolveDwmapiPath(icarusContentPath);
        if (!File.Exists(dllPath) || !File.Exists(dwmapiPath))
        {
            return new Ue4ssLoaderStatus(IsInstalled: false, InstalledVersion: null);
        }

        var logPath = Ue4ssGamePaths.ResolveLoaderLogPath(icarusContentPath);
        var version = File.Exists(logPath) ? Ue4ssLogVersionParser.Parse(File.ReadLines(logPath)) : null;
        return new Ue4ssLoaderStatus(IsInstalled: true, InstalledVersion: version);
    }

    public Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() => InstallOrUpdateCore(icarusContentPath, downloadedZipPath, backupDirectory), cancellationToken);

    private static void InstallOrUpdateCore(string icarusContentPath, string downloadedZipPath, string backupDirectory)
    {
        var win64Folder = Ue4ssGamePaths.ResolveWin64Folder(icarusContentPath);
        var loaderFolder = Ue4ssGamePaths.ResolveLoaderFolder(icarusContentPath);
        var dwmapiPath = Ue4ssGamePaths.ResolveDwmapiPath(icarusContentPath);
        var ue4ssBackupDirectory = Path.Combine(backupDirectory, "UE4SS-Loader");

        FolderBackup.BackupFolder(loaderFolder, ue4ssBackupDirectory);
        FolderBackup.BackupFile(dwmapiPath, ue4ssBackupDirectory);

        Directory.CreateDirectory(win64Folder);
        Directory.CreateDirectory(loaderFolder);

        using var archive = ZipFile.OpenRead(downloadedZipPath);
        var sizeBudget = new ExmodSizeBudget("UE4SS release archive");

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue; // a directory entry, nothing to write
            }

            var relativePath = entry.FullName.Replace('\\', '/');
            var segments = relativePath.Split('/');
            var isTopLevelDwmapi = segments.Length == 1 && segments[0].Equals("dwmapi.dll", StringComparison.OrdinalIgnoreCase);
            var isUnderMods = segments[0].Equals("Mods", StringComparison.OrdinalIgnoreCase);
            var isSettingsIni = segments.Length == 1 && segments[0].Equals("UE4SS-settings.ini", StringComparison.OrdinalIgnoreCase);

            var destPath = isTopLevelDwmapi
                ? AssetPathGuard.ResolveWithinDirectory(win64Folder, "dwmapi.dll")
                : AssetPathGuard.ResolveWithinDirectory(loaderFolder, relativePath);

            // Additive-only for anything under Mods\ (the framework's own built-in mods, plus
            // mods.txt/mods.json which track a real user's enabled state) and for
            // UE4SS-settings.ini (a real user's own customized settings) — never overwrite either.
            // Everything else (UE4SS.dll/dwmapi.dll themselves, README/Changelog/LICENSE) always
            // gets the fresh copy, since that's the actual point of an update.
            var additiveOnly = isUnderMods || isSettingsIni;
            if (additiveOnly && (File.Exists(destPath) || Directory.Exists(destPath)))
            {
                continue;
            }

            sizeBudget.Charge($"UE4SS release entry '{entry.FullName}'", entry.Length);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var entryStream = entry.Open();
            using var fileStream = File.Create(destPath);
            BoundedZipEntryCopy.CopyBounded(entryStream, fileStream, entry.Length, entry.FullName);
        }
    }

    public IReadOnlyList<string> ListUserAddedMods(string icarusContentPath)
    {
        var modsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);
        if (!Directory.Exists(modsFolder))
        {
            return [];
        }

        var frameworkOwned = ReadFrameworkModNames(modsFolder);
        return [.. Directory.GetDirectories(modsFolder)
            .Select(d => Path.GetFileName(d)!)
            .Where(name => !frameworkOwned.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    public Task<Ue4ssUninstallResult> UninstallAsync(
        string icarusContentPath, string stagedModsDirectory, string backupDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var loaderFolder = Ue4ssGamePaths.ResolveLoaderFolder(icarusContentPath);
            var dwmapiPath = Ue4ssGamePaths.ResolveDwmapiPath(icarusContentPath);
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);

            // Ordering is the safety argument, so it's spelled out:
            // 1. PRESERVE — every user-added mod is copied into this app's staging first, so even
            //    a failure right after this point has lost nothing of the user's.
            // 2. BACKUP the whole ue4ss folder + dwmapi.dll (keep-last-5, the installer's own
            //    rotation) — the full-undo path, framework mods and all.
            // 3. DELETE — only after both of the above have succeeded.
            var preservedMods = new List<string>();
            foreach (var modName in ListUserAddedMods(icarusContentPath))
            {
                var target = UniqueDirectoryName(stagedModsDirectory, modName);
                FolderBackup.CopyDirectory(Path.Combine(modsFolder, modName), Path.Combine(stagedModsDirectory, target));
                preservedMods.Add(target);
            }

            var ue4ssBackupDirectory = Path.Combine(backupDirectory, "UE4SS-Loader");
            FolderBackup.BackupFolder(loaderFolder, ue4ssBackupDirectory);
            FolderBackup.BackupFile(dwmapiPath, ue4ssBackupDirectory);

            if (Directory.Exists(loaderFolder))
            {
                Directory.Delete(loaderFolder, recursive: true);
            }

            if (File.Exists(dwmapiPath))
            {
                File.Delete(dwmapiPath);
            }

            return new Ue4ssUninstallResult(preservedMods, ue4ssBackupDirectory);
        }, cancellationToken);

    /// <summary>
    /// Framework-owned = listed in UE4SS's OWN mods.json (its bundled mod manifest — confirmed
    /// against a real install: exactly the 8 framework mods appear there, user-added ones don't),
    /// plus the shared\ infrastructure folder, which is framework code rather than a mod. The
    /// deliberate failure direction: a mod we can't classify counts as USER-ADDED and gets
    /// preserved — worst case some framework mods survive in staging, never the reverse.
    /// </summary>
    private static HashSet<string> ReadFrameworkModNames(string modsFolder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shared" };

        try
        {
            var modsJsonPath = Path.Combine(modsFolder, "mods.json");
            if (File.Exists(modsJsonPath))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(modsJsonPath));
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.TryGetProperty("mod_name", out var name) && name.GetString() is { } modName)
                    {
                        names.Add(modName);
                    }
                }
            }
        }
        catch (Exception)
        {
            // A malformed mods.json just means nothing beyond shared\ classifies as framework —
            // which errs toward preserving, per the contract above.
        }

        return names;
    }

    private static string UniqueDirectoryName(string rootDirectory, string desiredName)
    {
        var candidate = desiredName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(rootDirectory, candidate)))
        {
            candidate = $"{desiredName}_{++suffix}";
        }

        return candidate;
    }
}
