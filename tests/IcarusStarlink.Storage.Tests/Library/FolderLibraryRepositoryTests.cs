using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Library;

public class FolderLibraryRepositoryTests : IDisposable
{
    private readonly string _extractedModsDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _metaDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_meta");
    private readonly string _sourceDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_src");

    private FolderLibraryRepository CreateRepository() =>
        new(_extractedModsDir, _metaDir, NullLogger<FolderLibraryRepository>.Instance);

    private static ExmodPackageContents BuildFixture(string fileName = "Faster_Processors", string name = "Faster Processors")
    {
        var package = new ExmodPackage
        {
            Name = name, Author = "TestAuthor", Version = "1.0", Description = "Speeds things up.", FileName = fileName,
            Rows =
            [
                new ExmodFileRow
                {
                    CurrentFile = "Crafting-D_ProcessorRecipes.json",
                    FileItems = [new ExmodFileItem { Name = "SmelterRecipe", Fields = { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(5) } }],
                },
            ],
        };

        var assets = new List<ExmodAssetEntry> { new("readme.md", "Speeds up recipes."u8.ToArray()) };
        return new ExmodPackageContents(package, assets);
    }

    [Fact]
    public void Import_FromFolder_AddsEntryToLibrary()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();

        var entry = repo.Import(_sourceDir);

        Assert.Equal("Faster Processors", entry.Name);
        Assert.Single(repo.GetAll());
    }

    [Fact]
    public void Import_FromExmodz_AddsEntryToLibrary()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.EXMODZ");
        ExmodzArchive.Write(zipPath, BuildFixture());
        using var repo = CreateRepository();

        try
        {
            var entry = repo.Import(zipPath);
            Assert.Equal("Faster Processors", entry.Name);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void Import_PakFile_ThrowsNotSupported()
    {
        using var repo = CreateRepository();

        Assert.Throws<NotSupportedException>(() => repo.Import("something.pak"));
    }

    [Fact]
    public void Import_FolderNameCollision_DisambiguatesWithSuffix()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();

        var first = repo.Import(_sourceDir);
        var second = repo.Import(_sourceDir);

        Assert.NotEqual(first.FolderName, second.FolderName);
        Assert.Equal(2, repo.GetAll().Count);
        // Both keep the same internal EXMOD identity even though their folders differ.
        Assert.Equal(first.FileName, second.FileName);
    }

    [Fact]
    public void UpdateMetadata_PersistsAcrossRepositoryInstances()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        string folderName;
        using (var repo = CreateRepository())
        {
            folderName = repo.Import(_sourceDir).FolderName;
            repo.UpdateMetadata(folderName, isPinned: true, isFavorite: true, notes: "great mod");
        }

        using var reopened = CreateRepository();
        var entry = reopened.GetAll().Single();

        Assert.True(entry.IsPinned);
        Assert.True(entry.IsFavorite);
        Assert.Equal("great mod", entry.Notes);
    }

    [Fact]
    public void Delete_RemovesEntryFromLibraryAndDisk()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var entry = repo.Import(_sourceDir);

        repo.Delete(entry.FolderName);

        Assert.Empty(repo.GetAll());
        Assert.False(Directory.Exists(Path.Combine(_extractedModsDir, entry.FolderName)));
    }

    [Theory]
    [InlineData("faster")] // matches name
    [InlineData("testauthor")] // matches author
    [InlineData("smelterrecipe")] // matches EXMOD row/item content
    public void Search_MatchesAcrossNameAuthorAndContent(string query)
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        repo.Import(_sourceDir);

        var results = repo.Search(query);

        Assert.Single(results);
    }

    [Fact]
    public void Search_MatchesNotes()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var entry = repo.Import(_sourceDir);
        repo.UpdateMetadata(entry.FolderName, isPinned: false, isFavorite: false, notes: "overpowered but fun");

        var results = repo.Search("overpowered");

        Assert.Single(results);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        repo.Import(_sourceDir);

        Assert.Empty(repo.Search("nonexistentxyz"));
    }

    [Fact]
    public void Search_BlankQuery_ReturnsEverything()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        repo.Import(_sourceDir);

        Assert.Single(repo.Search("   "));
    }

    [Fact]
    public void Search_QueryWithFts5SpecialCharacters_DoesNotThrow()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        repo.Import(_sourceDir);

        // FTS5 MATCH treats these as operator syntax if not escaped — must not crash.
        var exception = Record.Exception(() => repo.Search("\"unterminated AND () * - :"));
        Assert.Null(exception);
    }

    [Fact]
    public void ListAssetPaths_And_ReadAssetContent_ReturnImportedFiles()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var entry = repo.Import(_sourceDir);

        var paths = repo.ListAssetPaths(entry.FolderName);
        var content = repo.ReadAssetContent(entry.FolderName, "readme.md");

        Assert.Contains("readme.md", paths);
        Assert.Equal("Speeds up recipes.", System.Text.Encoding.UTF8.GetString(content));
    }

    [Fact]
    public void UpdateMetadata_SidecarNeverAppearsInTheModsOwnAssetList()
    {
        // Regression: the sidecar must live outside Extracted_Mods\<folder>\, otherwise
        // ExmodFolder.ListAssetPaths (which treats everything but the .EXMOD as the mod's own
        // content) would show it as if it were part of the mod.
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var entry = repo.Import(_sourceDir);
        repo.UpdateMetadata(entry.FolderName, isPinned: true, isFavorite: true, notes: "n");

        var paths = repo.ListAssetPaths(entry.FolderName);

        Assert.DoesNotContain(paths, p => p.Contains("immmeta", StringComparison.OrdinalIgnoreCase));
        Assert.Single(paths); // just readme.md from the fixture
    }

    [Fact]
    public void Delete_AlsoRemovesMetadata_SoAFutureModWithTheSameFolderNameStartsClean()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var first = repo.Import(_sourceDir);
        repo.UpdateMetadata(first.FolderName, isPinned: true, isFavorite: true, notes: "should not survive");
        repo.Delete(first.FolderName);

        var second = repo.Import(_sourceDir); // re-imports with the same FileName -> same folder name

        Assert.Equal(first.FolderName, second.FolderName);
        Assert.False(second.IsPinned);
        Assert.False(second.IsFavorite);
        Assert.Equal("", second.Notes);
    }

    [Fact]
    public void ReadReadme_ReturnsReadmeContentByConvention()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var entry = repo.Import(_sourceDir);

        Assert.Equal("Speeds up recipes.", repo.ReadReadme(entry.FolderName));
    }

    [Fact]
    public void ReadReadme_NoReadmeFile_ReturnsNull()
    {
        var contents = BuildFixture();
        var noReadme = new ExmodPackageContents(contents.Package, []);
        ExmodFolder.Write(_sourceDir, noReadme);
        using var repo = CreateRepository();
        var entry = repo.Import(_sourceDir);

        Assert.Null(repo.ReadReadme(entry.FolderName));
    }

    [Fact]
    public void Constructor_FolderWithLockedExmodFile_SkipsItInsteadOfCrashingTheWholeApp()
    {
        // Regression: RescanAll runs in the constructor, which the app's default nav page
        // resolves before the main window is shown — a locked/mid-write folder (IOException,
        // not FormatException) must not be able to take down the whole app at launch.
        ExmodFolder.Write(_sourceDir, BuildFixture());
        Directory.CreateDirectory(_extractedModsDir);
        var lockedFolder = Path.Combine(_extractedModsDir, "Locked");
        ExmodFolder.Write(lockedFolder, BuildFixture(fileName: "Locked_Mod", name: "Locked Mod"));
        var exmodFilePath = Directory.EnumerateFiles(lockedFolder, "*.EXMOD", SearchOption.AllDirectories).Single();

        using var exclusiveLock = new FileStream(exmodFilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var repo = CreateRepository();

        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void GetAll_SkipsMalformedFolderInsteadOfThrowing()
    {
        Directory.CreateDirectory(_extractedModsDir);
        Directory.CreateDirectory(Path.Combine(_extractedModsDir, "NotAMod"));
        File.WriteAllText(Path.Combine(_extractedModsDir, "NotAMod", "readme.txt"), "no .EXMOD here");

        using var repo = CreateRepository();

        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void Delete_UnknownFolderName_ThrowsDirectoryNotFound()
    {
        using var repo = CreateRepository();

        Assert.Throws<DirectoryNotFoundException>(() => repo.Delete("DoesNotExist"));
    }

    private static string WriteFixturePakFile(string name = "MyPrebuiltMod")
    {
        // A per-call temp subdirectory keeps the actual filename (and so the derived mod name)
        // clean/predictable for assertions, rather than baking a uniqueness suffix into the name.
        var dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pakPath = Path.Combine(dir, $"{name}.pak");
        File.WriteAllBytes(pakPath, [1, 2, 3, 4]);
        return pakPath;
    }

    [Fact]
    public void ImportPak_AddsOpaqueEntryToLibrary()
    {
        var pakPath = WriteFixturePakFile("MyPrebuiltMod");
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath);

            Assert.Equal("MyPrebuiltMod", entry.Name);
            Assert.True(entry.IsOpaquePak);
            Assert.Single(repo.GetAll());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    [Fact]
    public void ImportPak_ListAssetPathsAndReadReadme_AreEmptyRatherThanThrowing()
    {
        var pakPath = WriteFixturePakFile();
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath);

            Assert.Empty(repo.ListAssetPaths(entry.FolderName));
            Assert.Null(repo.ReadReadme(entry.FolderName));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    [Fact]
    public void ImportPak_FolderNameCollision_DisambiguatesWithSuffix()
    {
        var pakPath = WriteFixturePakFile("SameName");
        using var repo = CreateRepository();

        try
        {
            var first = repo.ImportPak(pakPath);
            var second = repo.ImportPak(pakPath);

            Assert.NotEqual(first.FolderName, second.FolderName);
            Assert.Equal(2, repo.GetAll().Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    [Fact]
    public void Refresh_PicksUpAModFolderAddedExternallyAfterConstruction()
    {
        using var repo = CreateRepository();
        Assert.Empty(repo.GetAll());

        // Simulates a user dropping an already-extracted mod folder straight into Extracted_Mods
        // via Explorer while the app is running, bypassing Import() entirely.
        ExmodFolder.Write(Path.Combine(_extractedModsDir, "External_Mod"), BuildFixture(fileName: "External_Mod", name: "External Mod"));

        repo.Refresh();

        Assert.Contains(repo.GetAll(), e => e.Name == "External Mod");
    }

    [Fact]
    public void Constructor_FolderWithOnlyAPakFile_ScansAsAnOpaqueEntry()
    {
        // Simulates a prebuilt .pak dropped straight into Extracted_Mods outside the app, then
        // discovered on the next launch — not just via ImportPak() in the same session.
        Directory.CreateDirectory(_extractedModsDir);
        var folder = Path.Combine(_extractedModsDir, "External_Pak");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "External_Pak.pak"), [1, 2, 3]);

        using var repo = CreateRepository();

        var entry = Assert.Single(repo.GetAll());
        Assert.True(entry.IsOpaquePak);
        Assert.Equal("External_Pak", entry.Name);
    }

    [Fact]
    public void CreateBlankMod_AddsEntryWithNoRowsToLibrary()
    {
        using var repo = CreateRepository();

        var entry = repo.CreateBlankMod("My New Mod", "TestAuthor");

        Assert.Equal("My New Mod", entry.Name);
        Assert.Equal("TestAuthor", entry.Author);
        Assert.False(entry.IsOpaquePak);
        Assert.Single(repo.GetAll());
        Assert.Empty(repo.ListAssetPaths(entry.FolderName));
    }

    [Fact]
    public void CreateBlankMod_PersistsAsARealExmodPackageAcrossRepositoryInstances()
    {
        string folderName;
        using (var repo = CreateRepository())
        {
            folderName = repo.CreateBlankMod("My New Mod", "TestAuthor").FolderName;
        }

        using var reopened = CreateRepository();
        var package = ExmodFolder.Read(reopened.GetFolderPath(folderName)).Package;

        Assert.Equal("My New Mod", package.Name);
        Assert.Empty(package.Rows);
    }

    [Fact]
    public void CreateBlankMod_NameCollision_DisambiguatesWithSuffix()
    {
        using var repo = CreateRepository();

        var first = repo.CreateBlankMod("Same Name", "A");
        var second = repo.CreateBlankMod("Same Name", "A");

        Assert.NotEqual(first.FolderName, second.FolderName);
        Assert.Equal(2, repo.GetAll().Count);
    }

    [Fact]
    public void MarkLocallyEdited_SetsFlag_PersistsAcrossRepositoryInstances()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        string folderName;
        using (var repo = CreateRepository())
        {
            folderName = repo.Import(_sourceDir).FolderName;
            Assert.False(repo.GetAll().Single().IsLocallyEdited);

            repo.MarkLocallyEdited(folderName);
            Assert.True(repo.GetAll().Single().IsLocallyEdited);
        }

        using var reopened = CreateRepository();
        Assert.True(reopened.GetAll().Single().IsLocallyEdited);
    }

    [Fact]
    public void MarkLocallyEdited_UnknownFolderName_ThrowsDirectoryNotFound()
    {
        using var repo = CreateRepository();

        Assert.Throws<DirectoryNotFoundException>(() => repo.MarkLocallyEdited("DoesNotExist"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_extractedModsDir))
        {
            Directory.Delete(_extractedModsDir, recursive: true);
        }

        if (Directory.Exists(_sourceDir))
        {
            Directory.Delete(_sourceDir, recursive: true);
        }

        if (Directory.Exists(_metaDir))
        {
            Directory.Delete(_metaDir, recursive: true);
        }
    }
}
