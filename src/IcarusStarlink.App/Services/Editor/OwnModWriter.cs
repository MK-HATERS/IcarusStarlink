using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Writes one of the user's own mods from an editor (the mod editor window, the Editor page):
/// the "edited here" mark, the .EXMOD, and the Library refresh, in that order. Refuses when the
/// mod's .EXMOD changed since the caller read it (the other editor saved into it meanwhile), so
/// one editor never silently overwrites what the other saved.
/// </summary>
/// <param name="messenger">Where the Library change is announced (the shared messenger by default).</param>
public sealed class OwnModWriter(ILibraryRepository repository, IMessenger? messenger = null)
{
    /// <summary>When the mod's .EXMOD was last written; default when it has none yet.</summary>
    public DateTime StampOf(string folderName, string fileName)
    {
        var folderPath = repository.GetFolderPath(folderName);
        var path = Path.Combine(folderPath, "Extracted Mods", $"{fileName}.EXMOD");
        if (File.Exists(path))
        {
            return File.GetLastWriteTimeUtc(path);
        }

        // A folder copied in from the classic mod manager keeps its .EXMOD at the top.
        return ModPackageFile.Find(folderPath) is { } found ? File.GetLastWriteTimeUtc(found) : default;
    }

    /// <summary>
    /// Writes the package. False (nothing written) when <paramref name="expectedStamp"/> is given
    /// and the .EXMOD on disk is no longer the one read then. Disk errors are thrown; when the
    /// mod's Library file can't be written, the mod isn't touched either.
    /// </summary>
    /// <param name="newStamp">The .EXMOD's stamp after writing, to pass next time.</param>
    public bool TrySave(string folderName, ExmodPackage package, DateTime? expectedStamp, out DateTime newStamp)
    {
        newStamp = StampOf(folderName, package.FileName);
        if (expectedStamp is { } expected && newStamp != expected)
        {
            return false;
        }

        // The ✎ mark first: it's what keeps a converted pak from being converted again over these
        // edits, so a mark that can't be written stops the save before the mod is touched.
        repository.MarkLocallyEdited(folderName);
        ModPackageFile.Write(repository.GetFolderPath(folderName), package);
        newStamp = StampOf(folderName, package.FileName);
        RefreshEntry(repository, folderName);
        (messenger ?? WeakReferenceMessenger.Default).Send(new LibraryChangedMessage());
        return true;
    }

    /// <summary>
    /// Reads the written mod into the Library again, so its search finds what it holds now. Best
    /// effort: the mod is written either way, and the next full refresh reads it.
    /// </summary>
    internal static void RefreshEntry(ILibraryRepository repository, string folderName)
    {
        try
        {
            repository.RefreshEntry(folderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            Serilog.Log.Warning(ex, "Couldn't read {Folder} into the Library again after writing it", folderName);
        }
    }
}
