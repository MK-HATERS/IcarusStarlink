using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.ViewModels;

// "IcarusStarlink.exe --import <zip or folder>": a mod author's own build, installed the way an update is.
public sealed partial class DownloadsViewModel
{
    /// <summary>
    /// Installs a build of a mod from a zip (or any archive Starlink reads) or a folder, the way an
    /// update from Nexus is installed: the copies it replaces (its UE4SS folders and data mods, found
    /// by name) are backed up and swapped out, the config's settings carried over with new ones
    /// marked, both halves kept linked, and the Nexus link kept, so the Library can show "Your
    /// build" while it is newer than Nexus. Nothing is downloaded. For mod authors who test builds
    /// before they're released (copying a build straight into Starlink's folders and the game's
    /// Mods folder skips all of that). Returns the status line.
    /// </summary>
    public async Task<string> ImportOwnBuildAsync(string path)
    {
        var status = await ImportOwnBuildCoreAsync(path);
        PendingDownloadStatusMessage = status;
        return status;
    }

    private async Task<string> ImportOwnBuildCoreAsync(string path)
    {
        var isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path))
        {
            return $"Couldn't import the build: '{path}' doesn't exist.";
        }

        var name = isFolder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path);
        var scratch = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_OwnBuild_{Guid.NewGuid():N}");
        try
        {
            // A copy or an unpacked archive: the classifier unpacks nested archives where it reads,
            // and that must never be the author's own folder.
            await Task.Run(() =>
            {
                if (isFolder)
                {
                    FolderBackup.CopyDirectory(path, scratch);
                }
                else
                {
                    AnyArchiveExtractor.ExtractToDirectory(path, scratch);
                }
            });

            var previous = FindCopiesOfOwnBuild(scratch, name, out var nexusModId);
            var source = nexusModId is null ? "Local" : PendingDownloadEntry.NexusSource;
            var result = await RunInstallStepAsync(() => _modReplacement.ReplaceAsync(
                new ModReplacementRequest(name, previous.LibraryFolders, previous.Ue4ssFolders, Pristine: false),
                async removeOld => (await ClassifyAndImportExtractedModAsync(scratch, Path.GetFileName(path), source, nexusModId, null, previous, removeOld)).Items));
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            if (!result.Succeeded)
            {
                var failed = result.Cancelled ? result.Message ?? "Cancelled." : $"Couldn't import the build '{name}': {result.Message}";
                _activityLog.Log(failed, ActivityEntryKind.Warning);
                return failed;
            }

            var landed = string.Join(", ", result.Items.Select(i => i.FolderName).Distinct(StringComparer.OrdinalIgnoreCase));
            var replaced = previous.LibraryFolders.Count + previous.Ue4ssFolders.Count;
            var done = replaced == 0
                ? $"Imported your build '{name}': {landed}. It wasn't installed before, so it is new in your Library."
                : $"Imported your build '{name}' in place of the installed copy: {landed}.{result.Summary}";
            _activityLog.Log(done, ActivityEntryKind.Success);
            return done;
        }
        catch (Exception ex)
        {
            var failed = $"Couldn't import the build '{name}': {ex.Message}";
            _activityLog.Log(failed, ActivityEntryKind.Warning);
            return failed;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception)
            {
                // Scratch space in %TEMP%; a locked leftover isn't worth reporting.
            }
        }
    }

    /// <summary>
    /// What a build replaces: installed UE4SS mods with the same folder names as its UE4SS mods, and
    /// Library mods named like one of its data files (folder, file, name, or one of a pak bundle's
    /// paks). When the build has data files, the data half that came in the same Nexus download as a
    /// replaced UE4SS mod goes too, so the halves stay one pair. The Nexus mod they are linked to
    /// (the first one found) is kept on the new copies.
    /// </summary>
    private PreviousCopies FindCopiesOfOwnBuild(string unpacked, string rootName, out int? nexusModId)
    {
        var (ue4ssNames, dataNames) = ExtractedModClassifier.ModNamesIn(unpacked, rootName);
        bool IsDataName(string? name) => name is not null && dataNames.Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase);

        var installedUe4ss = _ue4ssNames.All().Where(n => ue4ssNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var ue4ssFiles = installedUe4ss.Select(n => _ue4ssModMetaStore.Load(n))
            .Where(m => m.NexusModId is not null)
            .Select(m => (m.NexusModId, m.NexusFileId))
            .ToList();
        var library = _libraryRepository.GetAll()
            .Where(e => IsDataName(e.FolderName) || IsDataName(e.FileName) || IsDataName(e.Name) || e.PakFileNames.Any(IsDataName)
                || (dataNames.Count > 0 && e.NexusModId is not null && ue4ssFiles.Contains((e.NexusModId, e.NexusFileId))))
            .ToList();
        nexusModId = library.Select(e => e.NexusModId).FirstOrDefault(id => id is not null)
            ?? ue4ssFiles.Select(f => f.NexusModId).FirstOrDefault();
        return new PreviousCopies([.. library.Select(e => e.FolderName)], installedUe4ss);
    }
}
