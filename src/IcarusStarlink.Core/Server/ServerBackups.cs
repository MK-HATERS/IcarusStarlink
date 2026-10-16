using System.IO.Compression;

namespace IcarusStarlink.Core.Server;

public sealed record ServerBackupInfo(string Path, string Name, DateTime CreatedLocal, long SizeBytes);

/// <summary>
/// Whole-server backups: one zip of what a server owner can't get back — Saved\PlayerData (every
/// prospect with its rolling .backup files, Accolades), ServerSettings.ini, and the admin and ban
/// lists. Made automatically before an update, a mod change and a restore, and on demand. Restoring
/// first backs up the current state ("before-restore"), so a restore can always be undone.
/// </summary>
public static class ServerBackups
{
    public static string CreateBackup(string installFolder, string? savedDirSuffix, string backupsFolder, string reason)
    {
        Directory.CreateDirectory(backupsFolder);
        var saved = DedicatedServerLayout.SavedFolder(installFolder, savedDirSuffix);
        var safeReason = new string([.. reason.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')]);
        var zipPath = Path.Combine(backupsFolder, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{(safeReason.Length > 0 ? safeReason : "manual")}.zip");
        for (var n = 2; File.Exists(zipPath); n++)
        {
            zipPath = Path.Combine(backupsFolder, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{safeReason}_{n}.zip");
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var playerData = DedicatedServerLayout.PlayerDataFolder(installFolder, savedDirSuffix);
        if (Directory.Exists(playerData))
        {
            foreach (var file in Directory.EnumerateFiles(playerData, "*", SearchOption.AllDirectories))
            {
                AddShared(archive, file, Path.GetRelativePath(saved, file));
            }
        }

        foreach (var single in new[]
                 {
                     DedicatedServerLayout.SettingsIniPath(installFolder, savedDirSuffix),
                     DedicatedServerLayout.AdminWhitelistPath(installFolder, savedDirSuffix),
                     DedicatedServerLayout.BanListPath(installFolder, savedDirSuffix),
                 })
        {
            if (File.Exists(single))
            {
                AddShared(archive, single, Path.GetRelativePath(saved, single));
            }
        }

        return zipPath;
    }

    public static IReadOnlyList<ServerBackupInfo> List(string backupsFolder) =>
        Directory.Exists(backupsFolder)
            ? [.. new DirectoryInfo(backupsFolder).GetFiles("*.zip")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new ServerBackupInfo(f.FullName, Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTime, f.Length))]
            : [];

    /// <summary>Backs up the current state, then extracts the zip over Saved (the server must be stopped). Returns the safety backup's path.</summary>
    public static string Restore(string zipPath, string installFolder, string? savedDirSuffix, string backupsFolder)
    {
        var safety = CreateBackup(installFolder, savedDirSuffix, backupsFolder, "before-restore");
        var saved = Path.GetFullPath(DedicatedServerLayout.SavedFolder(installFolder, savedDirSuffix));
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
        {
            var target = Path.GetFullPath(Path.Combine(saved, entry.FullName));
            if (!target.StartsWith(saved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue; // never write outside Saved, whatever the zip says
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }

        return safety;
    }

    /// <summary>The running server may hold a prospect file open; read it with sharing rather than fail the whole backup.</summary>
    private static void AddShared(ZipArchive archive, string file, string entryName)
    {
        try
        {
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal).Open();
            source.CopyTo(target);
        }
        catch (IOException)
        {
            // Skipped: locked beyond sharing. The rest of the backup is still worth having.
        }
    }
}
