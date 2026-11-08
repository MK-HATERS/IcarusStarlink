using System.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.IO;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.Services;

/// <summary>A Library mod Delete took away: its folder name then, and the backup the delete took (what Undo puts back).</summary>
public sealed record DeletedLibraryMod(string FolderName, string BackupPath);

/// <summary>
/// The one way a Library mod's copy is taken away — Library's Delete, and the old copy a
/// replacement (update, reinstall) swaps out. Besides the folder and its Library info, what the app
/// keeps per folder name goes too: the remembered fix choices, the saved source pak and the pak
/// preview cache, so none of it is picked up by a different mod later landing under the same name.
/// The preview cache was only ever purged by Delete, so an updated pak kept showing the old preview.
/// Delete also forgets the cached Nexus picture (a replacement keeps it: an update is the same mod).
/// Undo brings a deleted mod back from the backup Delete took, with all of that but the preview
/// cache and the picture (both are made again when needed).
/// </summary>
public sealed class LibraryModRemover(
    ILibraryRepository library,
    IPrebuiltPakSourceStore? pakSourceStore = null,
    string? pakPreviewCacheDirectory = null,
    ModFixService? fixService = null,
    string? thumbnailCacheDirectory = null)
{
    /// <summary>
    /// Delete: backed up first (a mod that can't be backed up isn't deleted), then the folder and
    /// everything kept for it. The backup's label keeps the mod's Library info, and its saved source
    /// pak and fix choices are kept beside the backup, so Undo can bring it all back.
    /// </summary>
    public DeletedLibraryMod Delete(string folderName)
    {
        var backup = library.BackupMod(folderName, ModBackupReasons.BeforeDelete);
        KeepForUndo(folderName, backup);
        library.Delete(folderName);

        // Best-effort from here on: the mod is gone. A locked fix-choices file used to make the
        // Library report "Deleted 0 of 1" for a mod it had deleted.
        try
        {
            fixService?.Forget(folderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Couldn't forget the fix choices of '{Folder}'", folderName);
        }

        MoveSourcePakForUndo(folderName, backup);
        PurgePreviewCache(folderName);
        ForgetNexusPicture(folderName);
        return new DeletedLibraryMod(folderName, backup);
    }

    /// <summary>
    /// The saved source pak goes from the store to beside the backup: moved, not copied (it can be
    /// hundreds of MB and Delete runs on the UI thread), and only once the folder is gone, so a
    /// delete that fails leaves the mod its pak. Not moved, it's deleted as before.
    /// </summary>
    private void MoveSourcePakForUndo(string folderName, string backup)
    {
        try
        {
            if (Path.IsPathRooted(backup) && pakSourceStore?.TryGetPath(folderName) is { } pak)
            {
                File.Move(pak, backup + FolderBackup.SourcePakSuffix, overwrite: true);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Couldn't keep the saved source pak of '{Folder}' with its backup", folderName);
        }

        DeleteSourcePak(folderName);
    }

    /// <summary>
    /// Undo of Delete: the folder and its Library info from the backup (under a free name when a
    /// newer mod took the old one), then its source pak and fix choices under the name it came back
    /// as. Returns that name, or null when the backup is gone. The pak and the choices are best
    /// effort: the mod is back either way.
    /// </summary>
    public string? Undo(DeletedLibraryMod deleted)
    {
        if (Stage(deleted) is not { } staged)
        {
            return null;
        }

        var restored = PutBack(staged);
        PutBackKept(deleted, restored);
        return restored;
    }

    /// <summary>Undo's first step, the slow copy, safe off the UI thread: the backup copied out to a staging folder (null when the backup is gone).</summary>
    public StagedDeletedMod? Stage(DeletedLibraryMod deleted) => library.StageDeletedMod(deleted.FolderName, deleted.BackupPath);

    /// <summary>Undo's second step, on the UI thread like Import and Delete: the staged copy moved into the Library. Returns the folder name it came back as.</summary>
    public string PutBack(StagedDeletedMod staged) => library.PutBackStagedMod(staged);

    /// <summary>A staged copy that won't be put back after all.</summary>
    public void Discard(StagedDeletedMod staged) => library.DiscardStagedMod(staged);

    /// <summary>
    /// Undo's last step, safe off the UI thread (the pak can be large): the saved source pak and fix
    /// choices kept beside the backup, under the name the mod came back as. Best effort.
    /// </summary>
    public void PutBackKept(DeletedLibraryMod deleted, string restored)
    {
        try
        {
            var pak = deleted.BackupPath + FolderBackup.SourcePakSuffix;
            if (pakSourceStore is not null && File.Exists(pak))
            {
                pakSourceStore.Save(restored, pak);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Brought back '{Folder}' but couldn't put back its saved source pak", restored);
        }

        try
        {
            var choices = deleted.BackupPath + FolderBackup.FixChoicesSuffix;
            if (fixService is not null && File.Exists(choices))
            {
                fixService.PutChoices(restored, File.ReadAllText(choices));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Brought back '{Folder}' but couldn't put back its fix choices", restored);
        }
    }

    /// <summary>
    /// Keeps the fix choices beside the delete's backup (removed with it by the keep-5 trim and
    /// Clean up backups; the source pak joins them in MoveSourcePakForUndo). Best effort: without
    /// them Undo still brings the mod back, only without the remembered choices.
    /// </summary>
    private void KeepForUndo(string folderName, string backup)
    {
        if (!Path.IsPathRooted(backup))
        {
            return;
        }

        try
        {
            if (fixService?.ReadChoices(folderName) is { } choices)
            {
                AtomicFile.WriteAllText(backup + FolderBackup.FixChoicesSuffix, choices);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Couldn't keep the fix choices of '{Folder}' with its backup", folderName);
        }
    }

    /// <summary>The mod's cached Nexus picture, so a different mod later imported under the same folder name doesn't show it. Best-effort, like DeleteSourcePak.</summary>
    private void ForgetNexusPicture(string folderName)
    {
        if (thumbnailCacheDirectory is null)
        {
            return;
        }

        try
        {
            File.Delete(Path.Combine(thumbnailCacheDirectory, folderName + ".img"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Couldn't delete the cached Nexus picture of '{Folder}'", folderName);
        }
    }

    /// <summary>
    /// The old copy a replacement swaps out. Already backed up by the replacement; its fix choices
    /// carry over to the new copy, and its source pak is dealt with once the new copy is in (the
    /// import may have saved the new pak under the same name).
    /// </summary>
    public void RemoveForReplacement(string folderName)
    {
        library.Delete(folderName);
        PurgePreviewCache(folderName);
    }

    /// <summary>The mod's saved original pak, if it has one. Best-effort: a leftover is harmless clutter, and the mod itself is already gone.</summary>
    public void DeleteSourcePak(string folderName)
    {
        try
        {
            pakSourceStore?.Delete(folderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Couldn't delete the saved source pak of '{Folder}'", folderName);
        }
    }

    /// <summary>The mod's extracted pak preview (can be as large as the pak itself). Best-effort, like DeleteSourcePak.</summary>
    public void PurgePreviewCache(string folderName)
    {
        if (pakPreviewCacheDirectory is null)
        {
            return;
        }

        try
        {
            var folder = Path.Combine(pakPreviewCacheDirectory, folderName);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Couldn't clear the pak preview cache of '{Folder}'", folderName);
        }
    }
}
