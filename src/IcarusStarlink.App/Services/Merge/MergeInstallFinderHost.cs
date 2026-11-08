using System.IO;
using System.IO.Compression;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Saves;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.Services;

/// <summary>The real IBrokenModFinderHost: Merge &amp; Install's build and install, and the Saves page's backups.</summary>
/// <param name="worldBackupFolder">Where each slot's worlds (its Prospects folder) are zipped: the Saves page's slot backup holds only the slot's own files.</param>
public sealed class MergeInstallFinderHost(Func<MergeInstallViewModel> mergeInstall, ISaveRepository saves, IGameProcessChecker gameProcess, string worldBackupFolder)
    : IBrokenModFinderHost
{
    public bool IsGameRunning()
    {
        try
        {
            return gameProcess.IsRunning();
        }
        catch (Exception)
        {
            // The same rule as Merge & Install: a failing process query never blocks on its own.
            return false;
        }
    }

    public IReadOnlyList<string> QueuedFolderNames() => [.. mergeInstall().FinderQueue().Select(e => e.FolderName)];

    public Task<string?> InstallOnlyAsync(IReadOnlyCollection<string> folderNames, bool backUpWhatsThere) =>
        mergeInstall().InstallForFinderAsync(folderNames, backUpWhatsThere);

    public Task<string?> InstallWholeQueueAsync(IReadOnlyCollection<string> putBack, bool backUpWhatsThere) =>
        mergeInstall().InstallWholeQueueForFinderAsync(putBack, backUpWhatsThere);

    /// <summary>
    /// False while Merge &amp; Install builds: the queue refuses changes then, so the mod isn't out
    /// and the finder mustn't record it as off.
    /// </summary>
    public bool RemoveFromQueue(string folderName)
    {
        var page = mergeInstall();
        if (page.IsRebuilding)
        {
            return false;
        }

        page.RemoveFromQueueByFolderNames([folderName]);
        return true;
    }

    /// <summary>How many world zips each slot keeps.</summary>
    private const int WorldZipsKept = 3;

    /// <summary>
    /// Every save slot as the Saves page's Backup now does it (a "manual" zip, never pruned, that
    /// Saves › Restore puts back), plus a zip of the slot's worlds, which that backup doesn't hold.
    /// </summary>
    public IReadOnlyList<string> BackUpSaves()
    {
        var zips = new List<string>();
        foreach (var slot in saves.ListSlots())
        {
            zips.Add(saves.BackupSlotManually(slot.SteamId));
            var prospects = Path.Combine(slot.FolderPath, "Prospects");
            if (Directory.Exists(prospects) && Directory.EnumerateFileSystemEntries(prospects).Any())
            {
                Directory.CreateDirectory(worldBackupFolder);
                var name = $"{slot.SteamId}_worlds_before_find_broken_mod";
                var zip = FolderBackup.MakeUniqueTimestampedPath(worldBackupFolder, name, DateTimeOffset.Now, ".zip");
                // Written under a temp name, then moved: a half-written zip never looks like a backup.
                var temp = zip + ".partial";
                try
                {
                    ZipFile.CreateFromDirectory(prospects, temp);
                    File.Move(temp, zip);
                }
                catch
                {
                    TryDeleteFile(temp);
                    throw;
                }

                zips.Add(zip);
                // Each search zips every world, so the zips add up fast: only the newest few stay.
                // This search's own zip is never removed (a clock set back could sort it older).
                foreach (var old in FolderBackup.EnumerateFileBackups(worldBackupFolder, name, ".zip").Skip(WorldZipsKept)
                             .Where(p => !string.Equals(p, zip, StringComparison.OrdinalIgnoreCase)))
                {
                    TryDeleteFile(old);
                }
            }
        }

        return zips;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A zip open elsewhere stays until the next search.
        }
    }
}
