using System.Globalization;
using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Install;

/// <summary>What Settings › Clean up backups removed.</summary>
public sealed record BackupCleanupResult(int DeletedCount, long FreedBytes, int FailedCount);

/// <summary>
/// Keeps the app's Backups folder from growing without end. Two jobs: the merged pak's backups
/// are counted across its current AND earlier names (each rename used to start a fresh keep-5 of
/// its own, so old names piled up at ~130 MB a copy), and Settings › Clean up backups trims every
/// backup set down to its newest few. Only entries this app's own backup code names (a
/// "_yyyyMMdd-HHmmss" suffix, or a server backup's "yyyy-MM-dd_HHmmss_" prefix) are ever deleted;
/// anything else is left alone, and the newest backup of anything is always kept.
/// </summary>
public static class BackupCleanup
{
    /// <summary>Merged-pak (and its manifest's) backups kept on every install.</summary>
    public const int MergedPakBackupsKept = 3;

    /// <summary>Backups of each mod / merged pak / server kept by Clean up backups.</summary>
    public const int KeptPerSetOnCleanUp = 2;

    /// <summary>Unrecognized folders (Mods, UE4SS, Server\&lt;profile&gt;, removed-paks…) are looked into this deep at most.</summary>
    private const int MaxDepth = 4;

    private const string MergedPakSetKey = "\u0000merged-pak";
    private const string ServerZipSetKey = "\u0000server-zip";

    // FolderBackup's own names: "{base}_yyyyMMdd-HHmmss", an optional "_N" collision suffix, then the extension (files only).
    private static readonly Regex TimestampSuffix = new(
        @"^(?<base>.+)_(?<stamp>\d{8}-\d{6})(?:_(?<n>\d+))?(?<ext>\.[^.]+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Server backups and the server's removed paks/UE4SS mods: "yyyy-MM-dd_HHmmss_{reason or name}".
    private static readonly Regex DatedPrefix = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})_(?<time>\d{6})_(?<rest>.+)$",
        RegexOptions.CultureInvariant);

    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private sealed record BackupEntry(string Path, bool IsFolder, string SetKey, string SortStamp, int Suffix);

    /// <summary>Total size of every file under the folder (0 when it doesn't exist). Slow on a big folder — call it off the UI thread.</summary>
    public static long MeasureBytes(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", AllFiles))
        {
            try
            {
                total += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or locked since it was listed: not worth failing the total over.
            }
        }

        return total;
    }

    /// <summary>
    /// Keeps only the newest keepPerSet backups of each mod, merged pak, manifest and server under
    /// backupsRoot, and deletes the rest. Never fewer than 1, so the newest backup of anything stays.
    /// </summary>
    public static BackupCleanupResult CleanUp(string backupsRoot, int keepPerSet = KeptPerSetOnCleanUp)
    {
        keepPerSet = Math.Max(1, keepPerSet);
        var deleted = 0;
        var failed = 0;
        long freed = 0;
        if (!Directory.Exists(backupsRoot))
        {
            return new BackupCleanupResult(0, 0, 0);
        }

        foreach (var directory in BackupDirectories(backupsRoot, 0))
        {
            foreach (var stale in StaleEntries(ParseEntries(directory), keepPerSet))
            {
                var size = stale.IsFolder ? MeasureBytes(stale.Path) : SafeLength(stale.Path);
                if (TryDelete(stale))
                {
                    deleted++;
                    freed += size;
                }
                else
                {
                    failed++;
                }
            }
        }

        return new BackupCleanupResult(deleted, freed, failed);
    }

    /// <summary>
    /// The merged pak's backups in backupDirectory, under its current and every earlier name,
    /// trimmed to the newest keep overall. Called after Install backs up the game's merged pak.
    /// </summary>
    public static void PruneMergedPakBackups(string backupDirectory, int keep = MergedPakBackupsKept)
    {
        if (!Directory.Exists(backupDirectory))
        {
            return;
        }

        var mergedPakBackups = ParseEntries(backupDirectory).Where(e => e.SetKey == MergedPakSetKey);
        foreach (var stale in StaleEntries(mergedPakBackups, Math.Max(1, keep)))
        {
            TryDelete(stale);
        }
    }

    /// <summary>"572 MB", "1.6 GB" — the size the Settings Backups line shows.</summary>
    public static string FormatSize(long bytes)
    {
        const double Mb = 1024.0 * 1024;
        const double Gb = Mb * 1024;
        return bytes switch
        {
            >= (long)Gb => string.Format(CultureInfo.CurrentCulture, "{0:0.0} GB", bytes / Gb),
            >= (long)Mb => string.Format(CultureInfo.CurrentCulture, "{0:0} MB", bytes / Mb),
            _ => string.Format(CultureInfo.CurrentCulture, "{0:0} KB", Math.Ceiling(bytes / 1024.0)),
        };
    }

    /// <summary>backupsRoot plus every folder under it that isn't itself a backup (a backup's own contents are never looked into).</summary>
    private static IEnumerable<string> BackupDirectories(string directory, int depth)
    {
        yield return directory;
        if (depth >= MaxDepth)
        {
            yield break;
        }

        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var subdirectory in subdirectories)
        {
            if (IsReparsePoint(subdirectory) || Parse(subdirectory, isFolder: true) is not null)
            {
                continue;
            }

            foreach (var nested in BackupDirectories(subdirectory, depth + 1))
            {
                yield return nested;
            }
        }
    }

    private static List<BackupEntry> ParseEntries(string directory)
    {
        var entries = new List<BackupEntry>();
        try
        {
            entries.AddRange(Directory.GetFiles(directory).Select(f => Parse(f, isFolder: false)).OfType<BackupEntry>());
            entries.AddRange(Directory.GetDirectories(directory)
                .Where(d => !IsReparsePoint(d))
                .Select(d => Parse(d, isFolder: true))
                .OfType<BackupEntry>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder: nothing in it is touched.
        }

        return entries;
    }

    private static BackupEntry? Parse(string path, bool isFolder)
    {
        // Set keys are per folder: entries are only ever grouped with others from the same folder.
        var name = Path.GetFileName(path);

        var timestamped = TimestampSuffix.Match(name);
        if (timestamped.Success && !(isFolder && timestamped.Groups["ext"].Success))
        {
            var baseName = timestamped.Groups["base"].Value;
            var extension = timestamped.Groups["ext"].Value;
            var setKey = !isFolder && IsMergedPakName(baseName + extension)
                ? MergedPakSetKey
                : $"{(isFolder ? "dir" : "file")}|{baseName}{extension}".ToUpperInvariant();
            var suffix = timestamped.Groups["n"].Success && int.TryParse(timestamped.Groups["n"].Value, out var n) ? n : 1;
            return new BackupEntry(path, isFolder, setKey, timestamped.Groups["stamp"].Value.Replace("-", ""), suffix);
        }

        var dated = DatedPrefix.Match(name);
        if (dated.Success)
        {
            // Every zip in a server's folder is one set (one per reason would keep far more than asked);
            // a removed pak or UE4SS mod is a set of its own name.
            var rest = dated.Groups["rest"].Value;
            var setKey = !isFolder && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? ServerZipSetKey
                : $"{(isFolder ? "dir" : "file")}|{rest}".ToUpperInvariant();
            var stamp = dated.Groups["date"].Value.Replace("-", "") + dated.Groups["time"].Value;
            return new BackupEntry(path, isFolder, setKey, stamp, 1);
        }

        return null;
    }

    private static bool IsMergedPakName(string fileName) =>
        fileName.Equals(InstallManifestNames.MergedPakFileName, StringComparison.OrdinalIgnoreCase)
        || InstallManifestNames.LegacyMergedPakFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every entry beyond the newest keep of its set (entries all come from one folder).</summary>
    private static IEnumerable<BackupEntry> StaleEntries(IEnumerable<BackupEntry> entries, int keep) =>
        entries
            .GroupBy(e => e.SetKey)
            .SelectMany(set => set
                .OrderByDescending(e => e.SortStamp, StringComparer.Ordinal)
                .ThenByDescending(e => e.Suffix)
                .ThenByDescending(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase)
                .Skip(keep))
            .ToList();

    private static bool TryDelete(BackupEntry entry)
    {
        try
        {
            if (entry.IsFolder)
            {
                Directory.Delete(entry.Path, recursive: true);
            }
            else
            {
                File.Delete(entry.Path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // can't tell: treat as off limits
        }
    }
}
