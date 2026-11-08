using System.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The one way a Library mod's copy is taken away — Library's Delete, and the old copy a
/// replacement (update, reinstall) swaps out. Besides the folder and its Library info, what the app
/// keeps per folder name goes too: the remembered fix choices, the saved source pak and the pak
/// preview cache, so none of it is picked up by a different mod later landing under the same name.
/// The preview cache was only ever purged by Delete, so an updated pak kept showing the old preview.
/// Delete also forgets the cached Nexus picture (a replacement keeps it: an update is the same mod).
/// </summary>
public sealed class LibraryModRemover(
    ILibraryRepository library,
    IPrebuiltPakSourceStore? pakSourceStore = null,
    string? pakPreviewCacheDirectory = null,
    ModFixService? fixService = null,
    string? thumbnailCacheDirectory = null)
{
    /// <summary>Delete: backed up first (a mod that can't be backed up isn't deleted), then the folder and everything kept for it.</summary>
    public void Delete(string folderName)
    {
        library.BackupMod(folderName);
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

        DeleteSourcePak(folderName);
        PurgePreviewCache(folderName);
        ForgetNexusPicture(folderName);
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
