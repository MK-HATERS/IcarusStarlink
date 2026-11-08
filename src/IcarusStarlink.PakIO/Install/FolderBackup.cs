using System.Globalization;
using System.Text.RegularExpressions;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Timestamped backups with a keep-the-newest trim (5 unless a caller passes another count): used
/// before something is overwritten or deleted — files in the game's mods folder (InstallService),
/// Library mod folders, UE4SS mods and the loader, profiles. Public so Storage reuses it.
/// </summary>
public static class FolderBackup
{
    private const int MaxBackups = 5;

    /// <summary>
    /// Put between a folder backup's own name and a random id while it is being copied:
    /// "{name}_yyyyMMdd-HHmmss.partial-{32 hex}". No backup lookup matches that name, and
    /// BackupCleanup removes one left by a crash.
    /// </summary>
    internal const string PartialMarker = ".partial-";

    /// <summary>
    /// A Library mod backup's label sits beside it as "{backup folder}.label.json" (why it was kept,
    /// which version). BackupCleanup removes a label whose backup folder is gone.
    /// </summary>
    public const string LabelSuffix = ".label.json";

    /// <summary>
    /// Copies sourceFolder into backupBaseDirectory under a timestamped name, then prunes older
    /// backups of the same folder name beyond MaxBackups. No-op if sourceFolder doesn't exist. A
    /// copy that fails throws and leaves no backup behind; a stale backup that can't be pruned
    /// doesn't throw.
    /// </summary>
    /// <param name="keepBackup">A backup the trim leaves in place even when it is beyond MaxBackups (the one about to be put back); the next backup's trim counts it as usual.</param>
    public static void BackupFolder(string sourceFolder, string backupBaseDirectory, string? keepBackup = null)
    {
        if (!Directory.Exists(sourceFolder))
        {
            return;
        }

        Directory.CreateDirectory(backupBaseDirectory);
        var name = Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var backupPath = MakeUniqueTimestampedPath(backupBaseDirectory, name, DateTimeOffset.UtcNow);

        // Copied under a name no backup lookup matches and renamed only once whole: a copy that stops
        // partway (a full disk, a locked file) must never become the "latest backup" a restore puts back.
        var partialPath = backupPath + PartialMarker + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(sourceFolder, partialPath);
            Directory.Move(partialPath, backupPath);
        }
        catch
        {
            Directories.TryDelete(partialPath);
            throw;
        }

        PruneOldFolderBackups(backupBaseDirectory, name, keepBackup);
    }

    /// <summary>Copies sourceFile into backupDirectory under a timestamped name, then prunes older backups of the same base name beyond maxBackups (MaxBackups if not specified). No-op if sourceFile doesn't exist. Returns the backup path, or null if there was nothing to back up. A failed copy throws; a stale backup that can't be pruned doesn't.</summary>
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
    /// The number is one past the highest already used in that second, never a gap: a prune that
    /// deleted "_stamp" but kept "_stamp_2" would otherwise hand the next backup "_stamp", which
    /// sorts as older than the copy it is newer than (keep 1 then kept the older pak).
    /// </summary>
    public static string MakeUniqueTimestampedPath(string directory, string baseName, DateTimeOffset timestamp, string extension = "")
    {
        // Invariant: BackupCleanup reads the stamp back as a Gregorian date, and a PC set to another
        // calendar (Thai, Hijri) would otherwise write a different year.
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var highest = HighestCollisionNumber(directory, $"{baseName}_{stamp}", extension);
        if (highest == 0)
        {
            return Path.Combine(directory, $"{baseName}_{stamp}{extension}");
        }

        var suffix = highest;
        string path;
        do
        {
            path = Path.Combine(directory, $"{baseName}_{stamp}_{++suffix}{extension}");
        }
        while (File.Exists(path) || Directory.Exists(path));

        return path;
    }

    /// <summary>0 when nothing named stamped (+ extension) exists yet, 1 for the plain name, N for "_N".</summary>
    private static int HighestCollisionNumber(string directory, string stamped, string extension)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var pattern = new Regex($"^{Regex.Escape(stamped)}(?:_(?<n>\\d+))?{Regex.Escape(extension)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var highest = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, stamped + "*"))
        {
            var match = pattern.Match(Path.GetFileName(entry));
            if (match.Success)
            {
                highest = Math.Max(highest, match.Groups["n"].Success && int.TryParse(match.Groups["n"].Value, out var n) ? n : 1);
            }
        }

        return highest;
    }

    /// <summary>The one recursive copy (Core's Directories.Copy), kept under this name for its many callers.</summary>
    public static void CopyDirectory(string sourceDir, string destDir) => Directories.Copy(sourceDir, destDir);

    /// <summary>
    /// Exactly the backups MakeUniqueTimestampedPath produced for THIS base name — "{name}_yyyyMMdd-HHmmss"
    /// with an optional "_N" collision suffix — never a prefix glob: "Foo_*" also
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
            : NewestFirst(Directory.GetDirectories(backupDirectory)
                .Where(d => IsTimestampedBackupOf(Path.GetFileName(d), name)), extension: "");

    /// <summary>This base name + extension's file backups, newest first; empty when the directory doesn't exist.</summary>
    public static IReadOnlyList<string> EnumerateFileBackups(string backupDirectory, string baseName, string extension) =>
        !Directory.Exists(backupDirectory)
            ? []
            : NewestFirst(Directory.GetFiles(backupDirectory)
                .Where(f => IsTimestampedBackupOf(Path.GetFileName(f), baseName, extension)), extension);

    private static readonly Regex StampAndNumber = new(@"_(?<stamp>\d{8}-\d{6})(?:_(?<n>\d+))?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// By the stamp and collision number in the name, the way BackupCleanup orders them, not by
    /// creation time: NTFS gives a name re-created soon after a delete the deleted file's creation
    /// time ("tunneling"), so a fresh backup could sort as the oldest and be pruned at once.
    /// </summary>
    private static List<string> NewestFirst(IEnumerable<string> backups, string extension) =>
        backups
            .Select(path =>
            {
                var leaf = Path.GetFileName(path);
                var match = StampAndNumber.Match(leaf[..^extension.Length]);
                var n = match.Groups["n"].Success && int.TryParse(match.Groups["n"].Value, out var parsed) ? parsed : 1;
                return (Path: path, Stamp: match.Groups["stamp"].Value, N: n);
            })
            .OrderByDescending(b => b.Stamp, StringComparer.Ordinal)
            .ThenByDescending(b => b.N)
            .Select(b => b.Path)
            .ToList();

    /// <summary>
    /// Best effort, like BackupCleanup.PruneMergedPakBackups: the new backup is already made, and
    /// one old backup held open (FModel) or read-only must not turn it into a failed backup that
    /// stops an install or a save. What's left goes on the next backup's trim or Clean up backups.
    /// </summary>
    private static void PruneOldFolderBackups(string backupDirectory, string name, string? keepBackup)
    {
        var backups = EnumerateFolderBackups(backupDirectory, name);
        var keep = keepBackup is null ? null : Path.GetFullPath(keepBackup).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var stale in backups.Skip(MaxBackups)
            .Where(b => keep is null || !string.Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), keep, StringComparison.OrdinalIgnoreCase)))
        {
            Directories.TryDelete(stale);
        }
    }

    /// <summary>Best effort, for the same reason as PruneOldFolderBackups.</summary>
    private static void PruneOldFileBackups(string backupDirectory, string baseName, string extension, int maxBackups)
    {
        var backups = EnumerateFileBackups(backupDirectory, baseName, extension);

        foreach (var stale in backups.Skip(maxBackups))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next backup's trim or Clean up backups.
            }
        }
    }
}
