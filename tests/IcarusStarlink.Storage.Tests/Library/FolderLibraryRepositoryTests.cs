using IcarusStarlink.Core.Library;
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
    private readonly string _backupsDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_backups");

    private FolderLibraryRepository CreateRepository() =>
        new(_extractedModsDir, _metaDir, _backupsDir, NullLogger<FolderLibraryRepository>.Instance);

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
    public void Construction_MalformedExmodJson_SkipsFolderInsteadOfCrashing()
    {
        // Found live, not by inspection: a single .EXMOD containing invalid JSON threw
        // JsonException straight through RescanAll's catch filter, crashing the whole app at
        // startup — the exact failure mode that catch exists to prevent.
        var brokenFolder = Path.Combine(_extractedModsDir, "BrokenMod");
        Directory.CreateDirectory(brokenFolder);
        File.WriteAllText(Path.Combine(brokenFolder, "BrokenMod.EXMOD"), "not valid json at all");
        ExmodFolder.Write(_sourceDir, BuildFixture());

        using var repo = CreateRepository();
        repo.Import(_sourceDir);

        Assert.Single(repo.GetAll());
        Assert.Equal("BrokenMod", Assert.Single(repo.UnreadableFolders));
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
    public void ImportPackage_AlreadyBuiltContents_AddsEntryToLibraryWithoutReadingFromDisk()
    {
        using var repo = CreateRepository();

        var entry = repo.ImportPackage(BuildFixture());

        Assert.Equal("Faster Processors", entry.Name);
        Assert.False(entry.IsOpaquePak);
        Assert.Single(repo.GetAll());
        // Real content actually landed on disk, not just in the in-memory cache — the same
        // real-write guarantee Import(string, ...) already has.
        Assert.True(Directory.Exists(Path.Combine(_extractedModsDir, entry.FolderName)));
    }

    [Fact]
    public void ImportPackage_ProvenanceTags_AreRecordedOnTheEntry()
    {
        using var repo = CreateRepository();

        var entry = repo.ImportPackage(BuildFixture(), source: "Nexus", nexusModId: 123, catalogEntryId: "abc");

        Assert.Equal("Nexus", entry.Source);
        Assert.Equal(123, entry.NexusModId);
        Assert.Equal("abc", entry.CatalogEntryId);
    }

    [Fact]
    public void ImportPackage_FolderNameCollision_DisambiguatesWithSuffix()
    {
        using var repo = CreateRepository();

        var first = repo.ImportPackage(BuildFixture());
        var second = repo.ImportPackage(BuildFixture());

        Assert.NotEqual(first.FolderName, second.FolderName);
        Assert.Equal(2, repo.GetAll().Count);
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

    /// <summary>Regression: deleting a mod with one file held open (an image editor, antivirus) deleted everything else, so its row stayed and the next scan showed it gutted or unreadable.</summary>
    [Fact]
    public void Delete_AFileIsLocked_LeavesTheModWhole()
    {
        using var repo = CreateRepository();
        var entry = repo.CreateBlankMod("Locked Mod", "me");
        var folder = repo.GetFolderPath(entry.FolderName);
        var notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, "mine");

        using (new FileStream(notes, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => repo.Delete(entry.FolderName));
        }

        Assert.Single(Directory.GetFiles(folder, "*.EXMOD", SearchOption.AllDirectories));
        Assert.True(File.Exists(notes));
        Assert.Equal([entry.FolderName], repo.GetAll().Select(e => e.FolderName));
        Assert.Single(repo.Search("Locked"));
    }

    /// <summary>Regression: a sidecar that couldn't be deleted made Delete fail after the mod's folder was already gone, leaving a row with no folder.</summary>
    [Fact]
    public void Delete_SidecarLocked_StillRemovesTheMod()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var entry = repo.Import(_sourceDir);

        using (new FileStream(Path.Combine(_metaDir, entry.FolderName + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            repo.Delete(entry.FolderName);
        }

        Assert.False(Directory.Exists(Path.Combine(_extractedModsDir, entry.FolderName)));
        Assert.Empty(repo.GetAll());
        Assert.Empty(repo.Search("faster"));
    }

    /// <summary>A delete whose last step failed last time leaves its folder beside Extracted_Mods; the next start clears it.</summary>
    [Fact]
    public void Construction_ClearsWhatAnEarlierDeleteLeft()
    {
        var leftover = Path.Combine(_extractedModsDir + ".deleting", "Old_Mod_123");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "Old_Mod.EXMOD"), "{}");

        using var repo = CreateRepository();

        Assert.False(Directory.Exists(leftover));
        Assert.Empty(repo.GetAll());
    }

    /// <summary>The merged pak's manifest lists an EXMOD by its declared name, so a rename must not lose it.</summary>
    [Fact]
    public void SetDisplayNameOverride_KeepsTheDeclaredName()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        string folderName;
        using (var repo = CreateRepository())
        {
            folderName = repo.Import(_sourceDir).FolderName;
            repo.SetDisplayNameOverride(folderName, "Mine");

            var entry = Assert.Single(repo.GetAll());
            Assert.Equal("Mine", entry.Name);
            Assert.Equal("Faster Processors", entry.DeclaredName);
        }

        using var reopened = CreateRepository();
        var again = Assert.Single(reopened.GetAll());
        Assert.Equal("Mine", again.Name);
        Assert.Equal("Faster Processors", again.DeclaredName);
    }

    [Fact]
    public void GetAll_ListTakenBeforeAnImportOrDelete_IsLeftAsItWas()
    {
        // Read on background threads (the risk pass, the fix service) while the UI thread imports
        // or deletes: the list handed out must never change under the reader.
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var first = repo.Import(_sourceDir);
        var before = repo.GetAll();

        var second = repo.Import(_sourceDir);
        var afterImport = repo.GetAll();
        repo.Delete(first.FolderName);

        Assert.Single(before);
        Assert.Equal(2, afterImport.Count);
        Assert.Equal([second.FolderName], repo.GetAll().Select(e => e.FolderName));
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

    /// <summary>Every word has to match: a word that is only punctuation is left out rather than matching nothing.</summary>
    [Fact]
    public void Search_TwoWordsWithFts5SpecialCharacters_MatchesOnTheRealWords()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        repo.Import(_sourceDir);

        Assert.Single(repo.Search("faster () processors -"));
        Assert.Single(repo.Search("\"faster\" proc*"));
    }

    /// <summary>Regression: each word widened the list (a mod matching any word showed), so "faster processors" listed every "faster" mod.</summary>
    [Fact]
    public void Search_TwoWords_MatchesOnlyModsWithBoth()
    {
        using var repo = CreateRepository();
        repo.ImportPackage(BuildFixture());
        repo.ImportPackage(BuildFixture(fileName: "Faster_Smelters", name: "Faster Smelters"));

        Assert.Equal(2, repo.Search("faster").Count);
        Assert.Equal(["Faster Processors"], repo.Search("faster processors").Select(e => e.Name));
        Assert.Equal(["Faster Smelters"], repo.Search("smelters FASTER").Select(e => e.Name));
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

    /// <summary>One download that ships several paks is ONE opaque mod named after the download, holding every pak — never a row per pak.</summary>
    [Fact]
    public void ImportPaks_SeveralPaksFromOneDownload_BecomeOneEntryNamedAfterTheDownload()
    {
        var iconPak = WriteFixturePakFile("ZZ_WirelessPower_Icon_P");
        var modelPak = Path.Combine(Path.GetDirectoryName(iconPak)!, "ZZ_WirelessPower_Model_P.pak");
        File.WriteAllBytes(modelPak, [9, 9, 9, 9, 9]);
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPaks([modelPak, iconPak], "WirelessPower", source: "Nexus", nexusModId: 361);

            Assert.Equal("WirelessPower", entry.Name);
            Assert.Equal("WirelessPower", entry.FolderName);
            Assert.True(entry.IsOpaquePak);
            Assert.Equal(["ZZ_WirelessPower_Icon_P.pak", "ZZ_WirelessPower_Model_P.pak"], entry.PakFileNames);
            Assert.Equal(361, entry.NexusModId);
            Assert.Contains("2 paks", entry.Description);

            repo.Refresh();
            var rescanned = Assert.Single(repo.GetAll());
            Assert.Equal("WirelessPower", rescanned.Name);
            Assert.Equal(2, rescanned.PakFileNames.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(iconPak)!, recursive: true);
        }
    }

    [Fact]
    public void ImportPak_SiblingMergeManifestPresent_RecordsMergedPackModNames()
    {
        var pakPath = WriteFixturePakFile("ISL-Merged_P");
        var manifestPath = Path.Combine(Path.GetDirectoryName(pakPath)!, IcarusStarlink.PakIO.InstallManifestNames.PakManifest);
        File.WriteAllText(manifestPath, "Includes the following mods:\nFaster Processors\nBigger Backpacks\n");
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath);

            Assert.True(entry.IsOpaquePak);
            Assert.NotNull(entry.MergedPackModNames);
            Assert.Equal(["Faster Processors", "Bigger Backpacks"], entry.MergedPackModNames);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    /// <summary>
    /// Regression guard: a Rebuild with an empty queue and at least one gameplay option enabled
    /// produces a manifest with a "Gameplay options applied:" section instead of/alongside the mods
    /// one — previously this left MergedPackModNames empty and the entry's own Description fell all
    /// the way to the generic "no EXMOD data" wording, indistinguishable from importing a totally
    /// unrelated prebuilt .pak, even though this really is the app's own Rebuild output.
    /// </summary>
    [Fact]
    public void ImportPak_OptionsOnlyManifest_RecordsMergedPackOptionDescriptionsAndDescribesItAccordingly()
    {
        var pakPath = WriteFixturePakFile("ISL-Merged_P");
        var manifestPath = Path.Combine(Path.GetDirectoryName(pakPath)!, IcarusStarlink.PakIO.InstallManifestNames.PakManifest);
        File.WriteAllText(manifestPath, "Gameplay options applied:\nStacks x2\nRemove Weight\n");
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath, mergedPackProfileName: "Weekend Build");

            Assert.True(entry.IsOpaquePak);
            Assert.True(entry.MergedPackModNames is null or { Count: 0 });
            Assert.Equal(["Stacks x2", "Remove Weight"], entry.MergedPackOptionDescriptions);
            Assert.Contains("built from gameplay options only", entry.Description);
            Assert.Contains("Stacks x2", entry.Description);
            Assert.DoesNotContain("no EXMOD data", entry.Description);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    [Fact]
    public void ImportPak_WithMergedPackProfileName_UsesItAsAuthorInsteadOfUnknown()
    {
        var pakPath = WriteFixturePakFile("ISL-Merged_P");
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath, mergedPackProfileName: "Weekend Build");

            Assert.Equal("Weekend Build", entry.Author);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    /// <summary>A Nexus download records its exact file and that file's version; an opaque pak shows the file's version (1.0.5) rather than the mod's headline one (1.0.8), and both survive a restart.</summary>
    [Fact]
    public void SetNexusFile_PersistsAndAnOpaquePakShowsTheFilesVersion()
    {
        var pakPath = WriteFixturePakFile();
        try
        {
            string folderName;
            using (var repo = CreateRepository())
            {
                var entry = repo.ImportPak(pakPath, source: "Nexus", nexusModId: 317);
                folderName = entry.FolderName;
                repo.SetNexusMetadata(folderName, "All In One", "Deyvid", null, "1.0.8");
                repo.SetNexusFile(folderName, 200, "1.0.5");

                var cached = repo.GetAll().Single(e => e.FolderName == folderName);
                Assert.Equal(200, cached.NexusFileId);
                Assert.Equal("1.0.5", cached.NexusFileVersion);
                Assert.Equal("1.0.5", cached.Version);
            }

            using var reopened = CreateRepository();
            var reloaded = reopened.GetAll().Single(e => e.FolderName == folderName);
            Assert.Equal(200, reloaded.NexusFileId);
            Assert.Equal("1.0.5", reloaded.NexusFileVersion);
            Assert.Equal("1.0.5", reloaded.Version);
            Assert.Equal("1.0.8", reloaded.NexusVersion);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    /// <summary>
    /// Nexus enrichment that runs after a rename or after the exact file was recorded (a later
    /// update check) used to put the Nexus page's name and headline version on the row until the
    /// next restart, where the rescan showed the rename and the file's version again.
    /// </summary>
    [Fact]
    public void SetNexusMetadata_AfterARenameAndAFileVersion_CachedEntryMatchesWhatARescanShows()
    {
        var pakPath = WriteFixturePakFile();
        try
        {
            using var repo = CreateRepository();
            var folderName = repo.ImportPak(pakPath, source: "Nexus", nexusModId: 317).FolderName;
            repo.SetDisplayNameOverride(folderName, "My Name For It");
            repo.SetNexusFile(folderName, 200, "1.0.5");

            repo.SetNexusMetadata(folderName, "All In One", "Deyvid", "Summary.", "1.0.8");

            var cached = repo.GetAll().Single(e => e.FolderName == folderName);
            Assert.Equal("My Name For It", cached.Name);
            Assert.Equal("1.0.5", cached.Version);
            Assert.Equal("Deyvid", cached.Author);

            repo.Refresh();
            var rescanned = repo.GetAll().Single(e => e.FolderName == folderName);
            Assert.Equal(rescanned.Name, cached.Name);
            Assert.Equal(rescanned.Version, cached.Version);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    /// <summary>A pak that fails to copy partway used to leave its new folder behind: on the next start it showed up as a mod with only some of the download's paks and no Library info.</summary>
    [Fact]
    public void ImportPaks_ACopyFailsPartway_LeavesNoFolderBehind()
    {
        var firstPak = WriteFixturePakFile("ZZ_First_P");
        var missingPak = Path.Combine(Path.GetDirectoryName(firstPak)!, "ZZ_Missing_P.pak");
        try
        {
            using var repo = CreateRepository();

            Assert.Throws<FileNotFoundException>(() => repo.ImportPaks([firstPak, missingPak], "TwoPaks"));
            Assert.Throws<FileNotFoundException>(() => repo.ImportPak(missingPak));

            Assert.Empty(Directory.GetDirectories(_extractedModsDir));
            repo.Refresh();
            Assert.Empty(repo.GetAll());
            Assert.Empty(repo.UnreadableFolders);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(firstPak)!, recursive: true);
        }
    }

    /// <summary>A directory where the sidecar goes makes its save fail.</summary>
    private void BlockSidecar(string folderName) => Directory.CreateDirectory(Path.Combine(_metaDir, folderName + ".json"));

    /// <summary>Regression: an import whose Library info couldn't be saved left its folder behind, and the next scan listed it as a mod with no source or link (a retry landed as "_2").</summary>
    [Fact]
    public void Import_SidecarSaveFails_LeavesNoFolderBehind()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        BlockSidecar("Faster_Processors");
        using var repo = CreateRepository();

        Assert.ThrowsAny<Exception>(() => repo.Import(_sourceDir));

        Assert.Empty(Directory.GetDirectories(_extractedModsDir));
        Assert.Empty(repo.GetAll());
        repo.Refresh();
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void CreateBlankMod_SidecarSaveFails_LeavesNoFolderBehind()
    {
        BlockSidecar("My_Mod");
        using var repo = CreateRepository();

        Assert.ThrowsAny<Exception>(() => repo.CreateBlankMod("My Mod", "me"));

        Assert.Empty(Directory.GetDirectories(_extractedModsDir));
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void ImportPaks_SidecarSaveFails_LeavesNoFolderBehind()
    {
        var firstPak = WriteFixturePakFile("ZZ_First_P");
        var secondPak = WriteFixturePakFile("ZZ_Second_P");
        BlockSidecar("TwoPaks");
        try
        {
            using var repo = CreateRepository();

            Assert.ThrowsAny<Exception>(() => repo.ImportPaks([firstPak, secondPak], "TwoPaks"));

            Assert.Empty(Directory.GetDirectories(_extractedModsDir));
            Assert.Empty(repo.GetAll());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(firstPak)!, recursive: true);
            Directory.Delete(Path.GetDirectoryName(secondPak)!, recursive: true);
        }
    }

    [Fact]
    public void ImportPak_SidecarSaveFails_LeavesNoFolderBehind()
    {
        var pak = WriteFixturePakFile("OnePak");
        BlockSidecar("OnePak");
        try
        {
            using var repo = CreateRepository();

            Assert.ThrowsAny<Exception>(() => repo.ImportPak(pak));

            Assert.Empty(Directory.GetDirectories(_extractedModsDir));
            Assert.Empty(repo.GetAll());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pak)!, recursive: true);
        }
    }

    [Fact]
    public void ImportPak_NoMergedPackProfileName_AuthorFallsBackToUnknown()
    {
        var pakPath = WriteFixturePakFile();
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath);

            Assert.Equal("Unknown", entry.Author);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    [Fact]
    public void ImportPak_NoSiblingMergeManifest_MergedPackModNamesIsNull()
    {
        var pakPath = WriteFixturePakFile();
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath);

            Assert.Null(entry.MergedPackModNames);
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
    public void Import_WithSource_RecordsItOnTheEntry()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();

        var entry = repo.Import(_sourceDir, source: "Database");

        Assert.Equal("Database", entry.Source);
    }

    [Fact]
    public void Import_NoSource_LeavesItNull()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();

        var entry = repo.Import(_sourceDir);

        Assert.Null(entry.Source);
    }

    [Fact]
    public void ImportPak_WithNexusModId_RecordsItOnTheEntry()
    {
        var pakPath = WriteFixturePakFile();
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath, source: "Nexus", nexusModId: 289);
            Assert.Equal(289, entry.NexusModId);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    [Fact]
    public void Import_WithCatalogEntryId_RecordsItAndSurvivesRenameAndRestart()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        string folderName;
        using (var repo = CreateRepository())
        {
            var entry = repo.Import(_sourceDir, source: "Database", catalogEntryId: "daedalus-abc123");
            folderName = entry.FolderName;
            Assert.Equal("daedalus-abc123", entry.CatalogEntryId);

            // The whole point of the stable ID: a Rename must not sever the catalog link.
            repo.SetDisplayNameOverride(folderName, "My Renamed Mod");
            Assert.Equal("daedalus-abc123", repo.GetAll().Single().CatalogEntryId);
        }

        using var reopened = CreateRepository();
        Assert.Equal("daedalus-abc123", reopened.GetAll().Single().CatalogEntryId);
    }

    [Fact]
    public void Import_WithNexusModId_RecordsItOnTheEntry()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();

        var entry = repo.Import(_sourceDir, source: "Nexus", nexusModId: 289);

        Assert.Equal(289, entry.NexusModId);
    }

    [Fact]
    public void ImportPak_WithSource_RecordsItOnTheEntry()
    {
        var pakPath = WriteFixturePakFile();
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath, source: "Nexus");
            Assert.Equal("Nexus", entry.Source);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    /// <summary>
    /// An ordinary author-declared EXMOD entry keeps its own Name/Author/Version (a Nexus lookup must
    /// never overwrite a real author's declared fields), but the Nexus VERSION is still surfaced as
    /// LibraryEntry.NexusVersion — the like-for-like side of update-checking, which used to be
    /// saved-but-never-read for this entry kind (the live "1.0" vs "1" false "Update available").
    /// Checked on the cached entry AND after a reopen.
    /// </summary>
    [Fact]
    public void SetNexusMetadata_ExmodEntry_SurfacesNexusVersionWithoutTouchingDeclaredFields()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        string folderName, declaredName, declaredVersion;
        using (var repo = CreateRepository())
        {
            var entry = repo.Import(_sourceDir, source: "Nexus", nexusModId: 339);
            folderName = entry.FolderName;
            declaredName = entry.Name;
            declaredVersion = entry.Version;
            Assert.Null(entry.NexusVersion);

            repo.SetNexusMetadata(folderName, "Nexus Page Name", "NexusAuthor", "Nexus summary.", "1");

            var cached = repo.GetAll().Single(e => e.FolderName == folderName);
            Assert.Equal("1", cached.NexusVersion);
            Assert.Equal(declaredName, cached.Name);
            Assert.Equal(declaredVersion, cached.Version);
        }

        using var reopened = CreateRepository();
        var reloaded = reopened.GetAll().Single(e => e.FolderName == folderName);
        Assert.Equal("1", reloaded.NexusVersion);
        Assert.Equal(declaredName, reloaded.Name);
        Assert.Equal(declaredVersion, reloaded.Version);
    }

    [Fact]
    public void SetNexusMetadata_OpaquePakEntry_OverridesNameAuthorDescription()
    {
        var pakPath = WriteFixturePakFile();
        using var repo = CreateRepository();

        try
        {
            var entry = repo.ImportPak(pakPath, source: "Nexus");

            repo.SetNexusMetadata(entry.FolderName, "Real Mod Name", "RealAuthor", "A real summary from Nexus.", "2.6");

            var updated = repo.GetAll().Single(e => e.FolderName == entry.FolderName);
            Assert.Equal("Real Mod Name", updated.Name);
            Assert.Equal("RealAuthor", updated.Author);
            Assert.Equal("A real summary from Nexus.", updated.Description);
            Assert.Equal("2.6", updated.Version);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    [Fact]
    public void SetNexusMetadata_PersistsAcrossRepositoryReopen()
    {
        var pakPath = WriteFixturePakFile();
        string folderName;

        using (var repo = CreateRepository())
        {
            var entry = repo.ImportPak(pakPath, source: "Nexus");
            folderName = entry.FolderName;
            repo.SetNexusMetadata(folderName, "Real Mod Name", "RealAuthor", "A real summary.", "2.6");
        }

        try
        {
            using var reopened = CreateRepository();
            var reloaded = reopened.GetAll().Single(e => e.FolderName == folderName);

            Assert.Equal("Real Mod Name", reloaded.Name);
            Assert.Equal("RealAuthor", reloaded.Author);
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

    [Fact]
    public void SetDisplayNameOverride_OverridesTheNameWithoutTouchingTheRealExmod_PersistsAcrossRepositoryInstances()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        string folderName;
        using (var repo = CreateRepository())
        {
            folderName = repo.Import(_sourceDir).FolderName;

            repo.SetDisplayNameOverride(folderName, "My Custom Name");
            Assert.Equal("My Custom Name", repo.GetAll().Single().Name);
        }

        using var reopened = CreateRepository();
        Assert.Equal("My Custom Name", reopened.GetAll().Single().Name);
        // The real .EXMOD's own "name" field is never touched — confirmed by reading it back
        // through ExmodFolder.Read directly rather than through the repository's own (now
        // overridden) Name.
        Assert.Equal("Faster Processors", ExmodFolder.Read(Path.Combine(reopened.GetFolderPath(folderName))).Package.Name);
    }

    [Fact]
    public void SetDisplayNameOverride_EmptyOrWhitespace_ClearsTheOverrideBackToTheDefaultName()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;
        repo.SetDisplayNameOverride(folderName, "Temporary Name");
        Assert.Equal("Temporary Name", repo.GetAll().Single().Name);

        repo.SetDisplayNameOverride(folderName, "   ");

        Assert.Equal("Faster Processors", repo.GetAll().Single().Name);
    }

    [Fact]
    public void SetDisplayNameOverride_UnknownFolderName_ThrowsDirectoryNotFound()
    {
        using var repo = CreateRepository();

        Assert.Throws<DirectoryNotFoundException>(() => repo.SetDisplayNameOverride("DoesNotExist", "Anything"));
    }

    [Fact]
    public void LinkToNexus_SetsIdAndSource_PersistsAcrossRepositoryInstances()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        string folderName;
        using (var repo = CreateRepository())
        {
            folderName = repo.Import(_sourceDir).FolderName;
            Assert.Null(repo.GetAll().Single().NexusModId);

            repo.LinkToNexus(folderName, 12345);

            var entry = repo.GetAll().Single();
            Assert.Equal(12345, entry.NexusModId);
            Assert.Equal("Nexus", entry.Source);
        }

        using var reopened = CreateRepository();
        var reopenedEntry = reopened.GetAll().Single();
        Assert.Equal(12345, reopenedEntry.NexusModId);
        Assert.Equal("Nexus", reopenedEntry.Source);
    }

    [Fact]
    public void LinkToNexus_UnknownFolderName_ThrowsDirectoryNotFound()
    {
        using var repo = CreateRepository();

        Assert.Throws<DirectoryNotFoundException>(() => repo.LinkToNexus("DoesNotExist", 123));
    }

    /// <summary>
    /// Backups used to be found with a "{folderName}_*" prefix glob, so mod "Foo" saw every
    /// backup of "Foo_2" — the exact name ModFolders.MakeUnique gives a re-import — and "restore"
    /// could overwrite Foo with Foo_2's content, while pruning Foo's backups could delete Foo_2's.
    /// </summary>
    [Fact]
    public void ModBackups_SiblingModWhoseNameIsAPrefix_NeverSeesOrPrunesTheOthersBackups()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var first = repo.Import(_sourceDir).FolderName;
        var second = repo.Import(_sourceDir).FolderName;
        Assert.Equal(first + "_2", second);

        repo.BackupMod(second);

        Assert.False(repo.HasModBackup(first));
        Assert.False(repo.RestoreLatestModBackup(first));

        for (var i = 0; i < 7; i++)
        {
            repo.BackupMod(first);
        }

        Assert.True(repo.HasModBackup(second), "pruning the first mod's backups must never touch the sibling's");
    }

    /// <summary>A sidecar that merely couldn't be read (locked) came back as defaults, and the pin toggle's read-modify-write then saved those defaults — dropping the mod's Nexus link.</summary>
    [Fact]
    public void UpdateMetadata_SidecarLockedRightNow_ThrowsAndLeavesTheSidecarUntouched()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir, source: "Nexus", nexusModId: 339).FolderName;
        var sidecarPath = Path.Combine(_metaDir, $"{folderName}.json");
        var before = File.ReadAllText(sidecarPath);

        using (new FileStream(sidecarPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => repo.UpdateMetadata(folderName, isPinned: true, isFavorite: false, notes: ""));
        }

        Assert.Equal(before, File.ReadAllText(sidecarPath));
        Assert.Equal(339, repo.GetAll().Single(e => e.FolderName == folderName).NexusModId);
    }

    /// <summary>Nexus enrichment changed the cached entry's name but not the FTS row, so an opaque pak was unsearchable by its displayed name until restart.</summary>
    [Fact]
    public void SetNexusMetadata_OpaquePakEntry_IsSearchableByItsNewNameImmediately()
    {
        var pakPath = WriteFixturePakFile();
        using var repo = CreateRepository();
        try
        {
            var entry = repo.ImportPak(pakPath, source: "Nexus");

            repo.SetNexusMetadata(entry.FolderName, "Cool Mod Reborn", "RealAuthor", "A summary.", "2.6");

            Assert.Contains(repo.Search("Reborn"), e => e.FolderName == entry.FolderName);
            Assert.Contains(repo.Search("RealAuthor"), e => e.FolderName == entry.FolderName);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pakPath)!, recursive: true);
        }
    }

    [Fact]
    public void MarkFixedByIsl_KeepsTheFirstTime_AndSurvivesARescan()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;

        repo.MarkFixedByIsl(folderName);
        var first = repo.GetAll().Single(e => e.FolderName == folderName).FixedByIslAtUtc;
        Thread.Sleep(20);
        repo.MarkFixedByIsl(folderName);
        repo.Refresh();

        Assert.NotNull(first);
        Assert.Equal(first, repo.GetAll().Single(e => e.FolderName == folderName).FixedByIslAtUtc);
    }

    [Fact]
    public void RestoreLatestModBackup_FromBeforeTheFirstFix_ClearsTheFixedMark_FromAfter_KeepsIt()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;

        repo.BackupMod(folderName);
        Thread.Sleep(20);
        repo.MarkFixedByIsl(folderName);
        Thread.Sleep(20);
        repo.BackupMod(folderName);
        Assert.True(repo.RestoreLatestModBackup(folderName));
        Assert.NotNull(repo.GetAll().Single(e => e.FolderName == folderName).FixedByIslAtUtc);

        // Drop the after-the-fix backup: the latest left is the one from before it.
        Directory.Delete(repo.TryGetLatestModBackupPath(folderName)!, recursive: true);

        Assert.True(repo.RestoreLatestModBackup(folderName));
        Assert.Null(repo.GetAll().Single(e => e.FolderName == folderName).FixedByIslAtUtc);
    }

    [Fact]
    public void HasModBackup_NoBackupYet_ReturnsFalse()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;

        Assert.False(repo.HasModBackup(folderName));
    }

    [Fact]
    public void BackupMod_ThenEditingTheRealFile_RestoreLatestModBackup_BringsBackTheOriginalContent()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;
        var folderPath = repo.GetFolderPath(folderName);
        var exmodPath = Directory.GetFiles(folderPath, "*.EXMOD", SearchOption.AllDirectories).Single();
        var originalContent = File.ReadAllText(exmodPath);

        repo.BackupMod(folderName);
        Assert.True(repo.HasModBackup(folderName));

        // Simulate a risky edit gone wrong — a real change to the mod's own file on disk.
        File.WriteAllText(exmodPath, "{ this is now corrupted }");

        var restored = repo.RestoreLatestModBackup(folderName);

        Assert.True(restored);
        Assert.Equal(originalContent, File.ReadAllText(exmodPath));
    }

    [Fact]
    public void Delete_ThenRestoreLatestModBackup_SidecarMetadataDoesNotComeBackOnItsOwn()
    {
        // A real bug found live (LibraryViewModel.GetUpdateAsync's own failure-recovery path):
        // Delete() removes the .immmeta.json sidecar (Source/CatalogEntryId/Pin/Favorite/Notes)
        // right alongside the mod's own folder, but RestoreLatestModBackup only ever restored the
        // folder — BackupMod never captured the sidecar in the first place, since a mod backup is
        // scoped to the mod's own folder. This test locks in that contract (the sidecar does NOT
        // come back on its own) so a caller that deletes-then-restores knows it must explicitly
        // re-apply metadata afterward, the way the real fix now does.
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;
        repo.SetCatalogEntry(folderName, "daedalus-abc123");
        repo.UpdateMetadata(folderName, isPinned: true, isFavorite: true, notes: "my notes");
        Assert.Equal("Database", repo.GetAll().Single().Source);

        repo.BackupMod(folderName);
        repo.Delete(folderName);
        Assert.Empty(repo.GetAll());

        var restored = repo.RestoreLatestModBackup(folderName);
        Assert.True(restored);

        var entryAfterRestore = repo.GetAll().Single();
        Assert.Null(entryAfterRestore.Source);
        Assert.Null(entryAfterRestore.CatalogEntryId);
        Assert.False(entryAfterRestore.IsPinned);
        Assert.False(entryAfterRestore.IsFavorite);
        Assert.Equal("", entryAfterRestore.Notes);

        // The fix: re-applying metadata via the same setters Import(source:,catalogEntryId:) would
        // have used brings the entry back to its exact pre-delete state.
        repo.SetCatalogEntry(folderName, "daedalus-abc123");
        repo.UpdateMetadata(folderName, isPinned: true, isFavorite: true, notes: "my notes");

        var entryAfterFix = repo.GetAll().Single();
        Assert.Equal("Database", entryAfterFix.Source);
        Assert.Equal("daedalus-abc123", entryAfterFix.CatalogEntryId);
        Assert.True(entryAfterFix.IsPinned);
        Assert.True(entryAfterFix.IsFavorite);
        Assert.Equal("my notes", entryAfterFix.Notes);
    }

    [Fact]
    public void RestoreLatestModBackup_NoBackupYet_ReturnsFalseAndLeavesTheModUntouched()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;

        var restored = repo.RestoreLatestModBackup(folderName);

        Assert.False(restored);
    }

    [Fact]
    public void RestoreLatestModBackup_ANewFileAddedSinceTheBackup_IsRemoved()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;
        repo.BackupMod(folderName);

        var strayFile = Path.Combine(repo.GetFolderPath(folderName), "should_not_survive.txt");
        File.WriteAllText(strayFile, "leftover from a bad edit");

        repo.RestoreLatestModBackup(folderName);

        // A real point-in-time restore, not a merge — a file added after the backup was taken
        // shouldn't survive, since the whole point is undoing everything since that snapshot.
        Assert.False(File.Exists(strayFile));
    }

    /// <summary>
    /// Regression: a backup that stopped partway (disk full) left a newer folder with no .EXMOD and
    /// no .pak, and Restore latest backup swapped the whole mod for it, so the mod dropped out as unreadable.
    /// </summary>
    [Fact]
    public void RestoreLatestModBackup_NewestBackupHasNoModFile_UsesTheLastWholeOne()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;
        var whole = repo.BackupMod(folderName);
        var partial = Path.Combine(_backupsDir, $"{folderName}_29991231-235959");
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "readme.md"), "copied before the disk filled up");

        Assert.Equal(whole, repo.TryGetLatestModBackupPath(folderName));
        Assert.True(repo.RestoreLatestModBackup(folderName));
        Assert.Equal("Faster Processors", Assert.Single(repo.GetAll()).Name);
        Assert.Empty(repo.UnreadableFolders);
    }

    /// <summary>Regression: a partial backup was the only one, so Restore latest backup replaced the whole mod with it.</summary>
    [Fact]
    public void RestoreLatestModBackup_OnlyBackupHasNoModFile_ReturnsFalseAndLeavesTheModUntouched()
    {
        ExmodFolder.Write(_sourceDir, BuildFixture());
        using var repo = CreateRepository();
        var folderName = repo.Import(_sourceDir).FolderName;
        Directory.CreateDirectory(Path.Combine(_backupsDir, $"{folderName}_20260101-120000"));

        Assert.False(repo.HasModBackup(folderName));
        Assert.False(repo.RestoreLatestModBackup(folderName));
        Assert.True(File.Exists(Path.Combine(repo.GetFolderPath(folderName), "readme.md")));
        Assert.Single(repo.GetAll());
    }

    /// <summary>Regression: "New mod" with a name holding ':' (or / ? * and the like) failed, because the name became the folder and file name as it was.</summary>
    [Fact]
    public void CreateBlankMod_NameWithCharactersAFileNameCantHold_MakesAReadableModThatKeepsTheName()
    {
        using var repo = CreateRepository();

        var entry = repo.CreateBlankMod("Stacks: x10", "Me");

        Assert.Equal("Stacks: x10", entry.Name);
        Assert.DoesNotContain(':', entry.FolderName);
        using var reopened = CreateRepository();
        Assert.Equal("Stacks: x10", Assert.Single(reopened.GetAll()).Name);
        Assert.Empty(reopened.UnreadableFolders);
    }

    /// <summary>
    /// Regression: the search only learned a mod's rows and items when it was imported or on a full
    /// rescan, so items saved into a mod afterwards weren't found until a restart.
    /// </summary>
    [Fact]
    public void RefreshEntry_AfterRowsAreWrittenIntoAMod_SearchFindsTheirItems()
    {
        using var repo = CreateRepository();
        var other = repo.CreateBlankMod("Other Mod", "Me");
        var entry = repo.CreateBlankMod("Faster Processors", "Me");
        Assert.Empty(repo.Search("SmelterRecipe"));
        ExmodFolder.Write(repo.GetFolderPath(entry.FolderName), BuildFixture(entry.FileName, "Faster Processors v2"));

        ILibraryRepository library = repo;
        library.RefreshEntry(entry.FolderName);

        Assert.Equal([entry.FolderName], repo.Search("SmelterRecipe").Select(e => e.FolderName));
        Assert.Contains(repo.GetAll(), e => e.FolderName == entry.FolderName && e.Name == "Faster Processors v2");
        // Only that one mod is read again: the others keep their cached entries.
        Assert.Same(other, repo.GetAll().Single(e => e.FolderName == other.FolderName));
        Assert.Equal(2, repo.GetAll().Count);
    }

    /// <summary>A mod whose folder is gone leaves the list and the search when it is refreshed.</summary>
    [Fact]
    public void RefreshEntry_FolderGone_DropsTheMod()
    {
        using var repo = CreateRepository();
        var entry = repo.CreateBlankMod("Gone Mod", "Me");
        Directory.Delete(repo.GetFolderPath(entry.FolderName), recursive: true);

        ILibraryRepository library = repo;
        library.RefreshEntry(entry.FolderName);

        Assert.Empty(repo.GetAll());
        Assert.Empty(repo.Search("Gone"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_extractedModsDir))
        {
            Directory.Delete(_extractedModsDir, recursive: true);
        }

        if (Directory.Exists(_extractedModsDir + ".deleting"))
        {
            Directory.Delete(_extractedModsDir + ".deleting", recursive: true);
        }

        if (Directory.Exists(_sourceDir))
        {
            Directory.Delete(_sourceDir, recursive: true);
        }

        if (Directory.Exists(_metaDir))
        {
            Directory.Delete(_metaDir, recursive: true);
        }

        if (Directory.Exists(_backupsDir))
        {
            Directory.Delete(_backupsDir, recursive: true);
        }
    }
}
