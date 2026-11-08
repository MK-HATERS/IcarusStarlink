using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Library's Delete and its Undo: the folder, its Library info, its saved source pak and its remembered fix choices all come back.</summary>
public sealed class LibraryModRemoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_remover");
    private readonly FolderLibraryRepository _library;
    private readonly PrebuiltPakSourceStore _sources;
    private readonly ModFixService _fixService;
    private readonly LibraryModRemover _remover;

    public LibraryModRemoverTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups", "Mods"), NullLogger<FolderLibraryRepository>.Instance);
        _sources = new PrebuiltPakSourceStore(Path.Combine(_root, "Source_Paks"));
        _fixService = new ModFixService(_library, Path.Combine(_root, "Data"), decisionsDirectory: Path.Combine(_root, "Fix_Choices"));
        _remover = new LibraryModRemover(_library, _sources, fixService: _fixService);
    }

    public void Dispose()
    {
        _library.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ImportMod()
    {
        var source = Path.Combine(_root, "src");
        ExmodFolder.Write(source, new ExmodPackageContents(new ExmodPackage { Name = "Mod A", Author = "Me", Version = "1.0", Description = "A test mod.", FileName = "ModA" }, []));
        return _library.Import(source).FolderName;
    }

    [Fact]
    public void Undo_PutsBackTheModWithItsNotesSourcePakAndFixChoices()
    {
        var folderName = ImportMod();
        _library.LinkToNexus(folderName, 77);
        _library.UpdateMetadata(folderName, isPinned: false, isFavorite: false, notes: "my notes");
        var pak = Path.Combine(_root, "orig.pak");
        File.WriteAllText(pak, "original pak");
        _sources.Save(folderName, pak);
        _fixService.PutChoices(folderName, "{\"choices\":1}");

        var deleted = _remover.Delete(folderName);

        Assert.Empty(_library.GetAll());
        Assert.Null(_sources.TryGetPath(folderName));
        Assert.Null(_fixService.ReadChoices(folderName));

        var restored = _remover.Undo(deleted);

        Assert.Equal(folderName, restored);
        var entry = Assert.Single(_library.GetAll());
        Assert.Equal("my notes", entry.Notes);
        Assert.Equal(77, entry.NexusModId);
        Assert.Equal("original pak", File.ReadAllText(_sources.TryGetPath(folderName)!));
        Assert.Equal("{\"choices\":1}", _fixService.ReadChoices(folderName));
    }

    /// <summary>A new mod took the name: the deleted one's pak and choices go with it to its new name, not onto the new mod.</summary>
    [Fact]
    public void Undo_NameTakenSince_KeepsThePakAndChoicesWithTheRestoredMod()
    {
        var folderName = ImportMod();
        var pak = Path.Combine(_root, "orig.pak");
        File.WriteAllText(pak, "original pak");
        _sources.Save(folderName, pak);
        _fixService.PutChoices(folderName, "{\"old\":1}");
        var deleted = _remover.Delete(folderName);
        Assert.Equal(folderName, ImportMod());

        var restored = _remover.Undo(deleted);

        Assert.NotNull(restored);
        Assert.NotEqual(folderName, restored);
        Assert.Null(_sources.TryGetPath(folderName));
        Assert.Null(_fixService.ReadChoices(folderName));
        Assert.Equal("original pak", File.ReadAllText(_sources.TryGetPath(restored)!));
        Assert.Equal("{\"old\":1}", _fixService.ReadChoices(restored));
    }
}
