using System.Globalization;
using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Keep-last-5 backup helper shared by every service that overwrites something in the real game
/// folder (InstallService's pak/UE4SS-mod-folder writes, Ue4ssLoaderInstallService's loader writes)
/// — extracted so the backup+prune algorithm only needs implementing once. Public (not internal)
/// so Storage's Ue4ssModRepository can reuse CopyDirectory too, rather than maintaining its own
/// separate copy of the same recursive-copy logic.
/// </summary>
public static class FolderBackup
{
    private const int MaxBackups = 5;

    /// <summary>Copies sourceFolder into backupBaseDirectory under a timestamped name, then prunes older backups of the same folder name beyond MaxBackups. No-op if sourceFolder doesn't exist.</summary>
    public static void BackupFolder(string sourceFolder, string backupBaseDirectory)
    {
        if (!Directory.Exists(sourceFolder))
        {
            return;
        }

        Directory.CreateDirectory(backupBaseDirectory);
        var name = Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var backupPath = MakeUniqueTimestampedPath(backupBaseDirectory, name, DateTimeOffset.UtcNow);
        CopyDirectory(sourceFolder, backupPath);
        PruneOldFolderBackups(backupBaseDirectory, name);
    }

    /// <summary>Copies sourceFile into backupDirectory under a timestamped name, then prunes older backups of the same base name beyond maxBackups (MaxBackups if not specified). No-op if sourceFile doesn't exist. Returns the backup path, or null if there was nothing to back up.</summary>
    public static string? BackupFile(string sourceFile, string backupDirectory, int maxBackups = MaxBackups)
    {
        if (!File.Exists(sourceFile))
        {
            return null;
        }

        Directory.CreateDirectory(backupDirectory);
        var baseName = Path.GetFileNameWithoutExtension(sourceFile);
        var extension = Path.GetExtension(sourceFile);
        var backupPath = MakeUniqueTimestampedPath(backupDirectory, baseName, DateTimeOffset.UtcNow, extension);
        File.Copy(sourceFile, backupPath, overwrite: false);

        PruneOldFileBackups(backupDirectory, baseName, extension, maxBackups);
        return backupPath;
    }

    /// <summary>
    /// Builds a timestamped path under directory that's guaranteed not to already exist as a file
    /// or folder — appends a numeric suffix instead of colliding when two backups land in the same
    /// second (a quick retry, or several sections saved back-to-back), so nothing already on disk
    /// is ever silently lost. Shared by every timestamped-backup call site in the app — this class's
    /// own folder/file backups, and IcarusStarlink.Storage's save-slot backups — so they all carry
    /// the same collision guarantee instead of each answering it differently (one prior copy of this
    /// logic here used to silently overwrite on collision instead).
    /// </summary>
    public static string MakeUniqueTimestampedPath(string directory, string baseName, DateTimeOffset timestamp, string extension = "")
    {
        // Invariant: BackupCleanup reads the stamp back as a Gregorian date, and a PC set to another
        // calendar (Thai, Hijri) would otherwise write a different year.
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{baseName}_{stamp}{extension}");
        var suffix = 1;
        while (File.Exists(path) || Directory.Exists(path))
        {
            path = Path.Combine(directory, $"{baseName}_{stamp}_{++suffix}{extension}");
        }

        return path;
    }

    public static void CopyDirectory(string sourceDir, string destDir)
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

    /// <summary>
    /// Exactly the backups MakeUniqueTimestampedPath produced for THIS base name — "{name}_yyyyMMdd-HHmmss"
    /// with an optional "_N" collision suffix — never a prefix glob (an audit finding): "Foo_*" also
    /// matched every backup of "Foo_2", the very name ModFolders.MakeUnique hands a re-import, so
    /// Foo's "restore from backup" could overwrite Foo with Foo_2's content, and pruning Foo's
    /// backups could delete Foo_2's only one. Same for files: profile "Main" vs "Main_Test".
    /// </summary>
    public static bool IsTimestampedBackupOf(string candidateLeafName, string baseName, string extension = "") =>
        Regex.IsMatch(
            candidateLeafName,
            $"^{Regex.Escape(baseName)}_\\d{{8}}-\\d{{6}}(_\\d+)?{Regex.Escape(extension)}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>This base name's folder backups, newest first; empty when the directory doesn't exist.</summary>
    public static IReadOnlyList<string> EnumerateFolderBackups(string backupDirectory, string name) =>
        !Directory.Exists(backupDirectory)
            ? []
            : Directory.GetDirectories(backupDirectory)
                .Where(d => IsTimestampedBackupOf(Path.GetFileName(d), name))
                .OrderByDescending(Directory.GetCreationTimeUtc)
                .ToList();

    /// <summary>This base name + extension's file backups, newest first; empty when the directory doesn't exist.</summary>
    public static IReadOnlyList<string> EnumerateFileBackups(string backupDirectory, string baseName, string extension) =>
        !Directory.Exists(backupDirectory)
            ? []
            : Directory.GetFiles(backupDirectory)
                .Where(f => IsTimestampedBackupOf(Path.GetFileName(f), baseName, extension))
                .OrderByDescending(File.GetCreationTimeUtc)
                .ToList();

    private static void PruneOldFolderBackups(string backupDirectory, string name)
    {
        var backups = EnumerateFolderBackups(backupDirectory, name);

        foreach (var stale in backups.Skip(MaxBackups))
        {
            Directory.Delete(stale, recursive: true);
        }
    }

    private static void PruneOldFileBackups(string backupDirectory, string baseName, string extension, int maxBackups)
    {
        var backups = EnumerateFileBackups(backupDirectory, baseName, extension);

        foreach (var stale in backups.Skip(maxBackups))
        {
            File.Delete(stale);
        }
    }
}
