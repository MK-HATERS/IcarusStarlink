using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Saving one of the user's own mods from an editor: where the .EXMOD goes, and what a locked Library file stops.</summary>
public sealed class OwnModWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "OwnModWriter_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    public OwnModWriterTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private OwnModWriter Writer() => new(_library, new WeakReferenceMessenger());

    /// <summary>A mod folder copied in from the classic mod manager keeps its .EXMOD at the top of the folder.</summary>
    private string ClassicFolder()
    {
        var folder = Path.Combine(_root, "Extracted_Mods", "Classic");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Classic.EXMOD"), ExmodJson.Serialize(new ExmodPackage
        {
            Name = "Classic", Author = "Someone", Version = "1", Description = "", FileName = "Classic",
            Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }],
        }));
        _library.Refresh();
        return folder;
    }

    /// <summary>Regression: a save wrote a second .EXMOD next to the classic one, and the mod dropped out of the Library.</summary>
    [Fact]
    public void Save_IntoAModCopiedFromTheClassicManager_LeavesOneExmod_AndTheModStaysInTheLibrary()
    {
        var folder = ClassicFolder();
        var writer = Writer();
        var package = ExmodFolder.ReadPackageOnly(folder);

        var stamp = writer.StampOf("Classic", package.FileName);
        Assert.NotEqual(default, stamp);

        package.Description = "changed";
        Assert.True(writer.TrySave("Classic", package, stamp, out _));

        Assert.Single(Directory.GetFiles(folder, "*.EXMOD", SearchOption.AllDirectories));
        _library.Refresh();
        Assert.Empty(_library.UnreadableFolders);
        Assert.Contains(_library.GetAll(), e => e.FolderName == "Classic");
        Assert.Equal("changed", ExmodFolder.ReadPackageOnly(folder).Description);
    }

    /// <summary>The "changed somewhere else" check works for a classic folder too: its stamp is the file that's really there.</summary>
    [Fact]
    public void Save_IntoAClassicFolderChangedSinceItWasRead_IsRefused()
    {
        var folder = ClassicFolder();
        var writer = Writer();
        var package = ExmodFolder.ReadPackageOnly(folder);
        var stamp = writer.StampOf("Classic", package.FileName);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "Classic.EXMOD"), DateTime.UtcNow.AddDays(-1));

        Assert.False(writer.TrySave("Classic", package, stamp, out _));
        Assert.True(File.Exists(Path.Combine(folder, "Classic.EXMOD")));
    }

    /// <summary>
    /// Regression: the .EXMOD was written first and the "edited here" mark after it, so a locked
    /// Library file reported "Couldn't save" for a save that had happened.
    /// </summary>
    [Fact]
    public void Save_WhenTheModsLibraryFileIsLocked_WritesNothing()
    {
        var entry = _library.CreateBlankMod("Mine", "Me", ModTemplate.Blank);
        var folderPath = _library.GetFolderPath(entry.FolderName);
        var writer = Writer();
        var package = ExmodFolder.ReadPackageOnly(folderPath);
        var stamp = writer.StampOf(entry.FolderName, package.FileName);
        package.Description = "changed";

        using (new FileStream(Path.Combine(_root, "Library_Meta", entry.FolderName + ".json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => writer.TrySave(entry.FolderName, package, stamp, out _));
        }

        var exmod = Directory.GetFiles(folderPath, "*.EXMOD", SearchOption.AllDirectories).Single();
        Assert.DoesNotContain("changed", File.ReadAllText(exmod));
        Assert.True(writer.TrySave(entry.FolderName, package, stamp, out _));
    }
}
