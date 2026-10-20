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
    /// <summary>The reason a daily automatic backup is filed under.</summary>
    public const string DailyReason = "daily";

    /// <summary>
    /// The profile's backups folder under <paramref name="serverBackupsRoot"/>: "&lt;name&gt;_&lt;id8&gt;".
    /// The name part follows the profile's CURRENT name, but the folder is found by its id: a folder
    /// made under an older name ("*_&lt;id8&gt;") is renamed to the current one, so renaming a profile
    /// never orphans its backups. When that rename can't happen (a file in it is open) the old
    /// folder is used as it is.
    /// </summary>
    public static string ResolveFolder(string serverBackupsRoot, DedicatedServerProfile profile)
    {
        var key = ServerFirewall.ProfileKey(profile);
        var safeName = new string([.. profile.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')]);
        var wanted = Path.Combine(serverBackupsRoot, $"{(safeName.Length > 0 ? safeName : "server")}_{key}");
        if (Directory.Exists(wanted) || !Directory.Exists(serverBackupsRoot))
        {
            return wanted;
        }

        var older = Directory.EnumerateDirectories(serverBackupsRoot, $"*_{key}")
            .Where(d => Path.GetFileName(d).EndsWith("_" + key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (older is null)
        {
            return wanted;
        }

        try
        {
            Directory.Move(older, wanted);
            return wanted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return older;
        }
    }

    /// <summary>"2026-10-16_171804_before-update_2" → "before-update"; null for a name that isn't one of these backups.</summary>
    public static string? ReasonOf(string backupName)
    {
        // yyyy-MM-dd_HHmmss_ is 18 characters.
        if (backupName.Length <= 18 || backupName[10] != '_' || backupName[17] != '_')
        {
            return null;
        }

        var reason = backupName[18..];
        var underscore = reason.LastIndexOf('_');
        if (underscore > 0 && reason[(underscore + 1)..].All(char.IsDigit))
        {
            reason = reason[..underscore];
        }

        return reason;
    }

    /// <summary>
    /// Keeps the newest <paramref name="keepPerReason"/> backups of each kind (manual, daily,
    /// before-update, before-mods, before-restore…) and deletes older ones; 0 keeps everything.
    /// The newest before-restore backup — the undo for the last restore — and anything named in
    /// <paramref name="neverDelete"/> are always kept. Zips this app didn't name are left alone.
    /// Returns the deleted paths.
    /// </summary>
    public static IReadOnlyList<string> Prune(string backupsFolder, int keepPerReason, IEnumerable<string>? neverDelete = null)
    {
        if (keepPerReason <= 0 || !Directory.Exists(backupsFolder))
        {
            return [];
        }

        var keep = new HashSet<string>(neverDelete ?? [], StringComparer.OrdinalIgnoreCase);
        var deleted = new List<string>();
        foreach (var group in List(backupsFolder).GroupBy(b => ReasonOf(b.Name), StringComparer.OrdinalIgnoreCase).Where(g => g.Key is not null))
        {
            foreach (var old in group.OrderByDescending(b => b.CreatedLocal).Skip(Math.Max(1, keepPerReason)))
            {
                if (keep.Contains(old.Path))
                {
                    continue;
                }

                try
                {
                    File.Delete(old.Path);
                    deleted.Add(old.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Open in another program: tried again after the next backup.
                }
            }
        }

        return deleted;
    }

    /// <summary>When the newest daily backup was made, or null when there is none.</summary>
    public static DateTime? LastDailyBackup(string backupsFolder) =>
        List(backupsFolder).Where(b => string.Equals(ReasonOf(b.Name), DailyReason, StringComparison.OrdinalIgnoreCase))
            .Select(b => (DateTime?)b.CreatedLocal)
            .Max();

    public static string CreateBackup(string installFolder, string? savedDirSuffix, string backupsFolder, string reason)
    {
        Directory.CreateDirectory(backupsFolder);
        var saved = DedicatedServerLayout.SavedFolder(installFolder, savedDirSuffix);
        var safeReason = new string([.. reason.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')]);
        if (safeReason.Length == 0)
        {
            safeReason = "manual";
        }

        // One timestamp for the base name and every _2, _3 … retry, so a retry that crosses a
        // second boundary can't name the zip after a different second (and the reason used for the
        // retries is the same "manual" fallback as the first try, not an empty one).
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(backupsFolder, $"{stamp}_{safeReason}.zip");
        for (var n = 2; File.Exists(zipPath); n++)
        {
            zipPath = Path.Combine(backupsFolder, $"{stamp}_{safeReason}_{n}.zip");
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
