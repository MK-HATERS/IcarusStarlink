using System.IO;
using System.Text.RegularExpressions;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Removes Starlink's own scratch folders and files from %TEMP% that are older than a day. Each
/// feature deletes its scratch in a finally block, so a crash, a kill or a power cut left it there
/// for good (Rebuild_* holds a copy of every queued mod). Only the exact names Starlink's own code
/// creates count, each ending in a 32-digit lowercase guid; never the update backup a failed
/// update's message points to, the world and server backups, the updater's log and outcome note,
/// or generic names another program could use (UE4SS_*.zip, UnrealPak-*.zip, *.EXMODZ).
/// </summary>
public static partial class TempScratchSweeper
{
    /// <summary>
    /// Never throws: whatever is locked or in use is skipped, and a folder left half deleted is
    /// finished next time. The age rule keeps it away from a still-running updater (it outlives the
    /// relaunch briefly) and from anything an orphaned UnrealPak still holds.
    /// </summary>
    public static void Sweep(string tempRoot, DateTime nowUtc, TimeSpan minAge)
    {
        var cutoff = nowUtc - minAge;
        var ownRoot = Path.Combine(tempRoot, "IcarusStarlink");
        SweepFolder(ownRoot, InOwnFolder(), cutoff);
        SweepFolder(Path.Combine(ownRoot, "CurveWriter"), GuidOnly(), cutoff);
        SweepFolder(tempRoot, InTemp(), cutoff);
    }

    private static void SweepFolder(string folder, Regex names, DateTime cutoffUtc)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (!names.IsMatch(entry.Name) || entry.CreationTimeUtc > cutoffUtc)
                {
                    continue;
                }

                if (entry is DirectoryInfo directory)
                {
                    DeleteFolder(directory);
                }
                else
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Locked or in use: tried again at the next start.
            }
        }
    }

    /// <summary>Mod files copied into scratch keep their read-only mark, which stops a plain recursive delete.</summary>
    private static void DeleteFolder(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                }
            }

            directory.Delete(recursive: true);
        }
    }

    /// <summary>In %TEMP%\IcarusStarlink.</summary>
    [GeneratedRegex("^(?:(?:AppUpdate|Rebuild|PrebuiltPakScratch|Manual|Peek|PakCompare|PakConvert)_[0-9a-f]{32}|Response_[0-9a-f]{32}\\.txt)$", RegexOptions.CultureInvariant)]
    private static partial Regex InOwnFolder();

    /// <summary>In %TEMP%\IcarusStarlink\CurveWriter.</summary>
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidOnly();

    /// <summary>In %TEMP% itself.</summary>
    [GeneratedRegex("^(?:IcarusStarlink_(?:Replace|Activate|OwnBuild|Import|Patch)_[0-9a-f]{32}|IcarusStarlink_AudioPreview_[0-9a-f]{32}\\.wav|IcarusStarlink-repoint-[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex InTemp();
}
