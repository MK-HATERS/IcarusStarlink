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

    public void RemoveFromQueue(string folderName) => mergeInstall().RemoveFromQueueByFolderNames([folderName]);

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
                var zip = FolderBackup.MakeUniqueTimestampedPath(worldBackupFolder, $"{slot.SteamId}_worlds_before_find_broken_mod", DateTimeOffset.Now, ".zip");
                // Written under a temp name, then moved: a half-written zip never looks like a backup.
                var temp = zip + ".partial";
                ZipFile.CreateFromDirectory(prospects, temp);
                File.Move(temp, zip);
                zips.Add(zip);
            }
        }

        return zips;
    }
}
