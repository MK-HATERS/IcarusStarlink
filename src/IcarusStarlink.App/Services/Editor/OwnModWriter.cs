using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Writes one of the user's own mods from an editor (the mod editor window, the Editor page):
/// the .EXMOD, the "edited here" mark, and the Library refresh, in that order. Refuses when the
/// mod's .EXMOD changed since the caller read it (the other editor saved into it meanwhile), so
/// one editor never silently overwrites what the other saved.
/// </summary>
public sealed class OwnModWriter(ILibraryRepository repository)
{
    /// <summary>When the mod's .EXMOD was last written; default when it has none yet.</summary>
    public DateTime StampOf(string folderName, string fileName)
    {
        var path = Path.Combine(repository.GetFolderPath(folderName), "Extracted Mods", $"{fileName}.EXMOD");
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
    }

    /// <summary>
    /// Writes the package. False (nothing written) when <paramref name="expectedStamp"/> is given
    /// and the .EXMOD on disk is no longer the one read then. Disk errors are thrown.
    /// </summary>
    /// <param name="newStamp">The .EXMOD's stamp after writing, to pass next time.</param>
    public bool TrySave(string folderName, ExmodPackage package, DateTime? expectedStamp, out DateTime newStamp)
    {
        newStamp = StampOf(folderName, package.FileName);
        if (expectedStamp is { } expected && newStamp != expected)
        {
            return false;
        }

        ExmodFolder.Write(repository.GetFolderPath(folderName), new ExmodPackageContents(package, []));
        newStamp = StampOf(folderName, package.FileName);
        repository.MarkLocallyEdited(folderName);
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        return true;
    }
}
