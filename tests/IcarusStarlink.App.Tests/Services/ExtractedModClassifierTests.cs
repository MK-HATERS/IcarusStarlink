using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// ExtractedModClassifier.ClassifyAndImport is the one place that decides what an already-extracted
/// archive actually IS — an EXMOD-shaped mod, a bare prebuilt .pak, or (the catch-all) a UE4SS mod —
/// purely from what's actually sitting on disk, never from the original archive's own file extension
/// or the dialog filter the user picked. It's shared by LibraryViewModel's manual "Import archive…"
/// and DownloadsViewModel's Nexus Activate flow, so a bug here is a bug in both places at once.
/// Fixtures build real folder trees (a real EXMOD via ExmodFolder.Write, a real bare .pak file, a
/// folder with neither) rather than mocking the filesystem — the classifier's own detection
/// (Directory.EnumerateFiles/GetFiles) is real I/O, so a fake would just be testing the fake.
/// </summary>
public sealed class ExtractedModClassifierTests : IDisposable
{
    private readonly string _extractedDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public ExtractedModClassifierTests() => Directory.CreateDirectory(_extractedDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_extractedDir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only — never the point of these tests.
        }
    }

    private static ExmodPackageContents BuildRealExmod(string fileName = "Faster_Processors") => new(
        ExmodJson.Parse($$"""
            {
                "name": "Faster Processors", "author": "Someone", "version": "1.0", "description": "D",
                "fileName": "{{fileName}}",
                "Rows": [
                    {"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                     "File_Items": [{"Name": "SmelterRecipe", "CraftTime": 5}]}
                ]
            }
            """),
        []);

    [Fact]
    public async Task ClassifyAndImport_FolderContainsAnExmodFile_ImportsThroughLibraryRepositoryAsAnExmodMod()
    {
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        var libraryRepository = new FakeLibraryRepository();
        var ue4ssModRepository = new FakeUe4ssModRepository();
        var prebuiltPakImporter = new FakePrebuiltPakImporter();

        var (entryName, folderName, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, ue4ssModRepository, prebuiltPakImporter, "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.False(isOpaquePak);
        Assert.Equal("Faster Processors", entryName);
        Assert.Equal("Faster_Processors", folderName);
        Assert.Equal(1, libraryRepository.ImportCallCount);
        Assert.Equal(_extractedDir, libraryRepository.LastImportSourcePath);
        // Never even looked at the pak/UE4SS branches once a real .EXMOD was found.
        Assert.Equal(0, prebuiltPakImporter.ImportCallCount);
        Assert.Equal(0, ue4ssModRepository.ImportFromFolderCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ExmodFileNestedInASubfolder_StillDetectedViaTheRecursiveScan()
    {
        // A mod author's own zip is very often "zip the whole mod folder", landing the .EXMOD one
        // (or more) levels deep rather than at the archive's own root — hasExmod's own scan is
        // SearchOption.AllDirectories specifically to cover this, not just a top-level check.
        var nestedDir = Path.Combine(_extractedDir, "MyModFolder", "Nested");
        Directory.CreateDirectory(nestedDir);
        ExmodFolder.Write(nestedDir, BuildRealExmod());
        var libraryRepository = new FakeLibraryRepository();

        var (_, _, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.False(isOpaquePak);
        Assert.Equal(1, libraryRepository.ImportCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ExmodExtensionLowercase_StillDetectedCaseInsensitively()
    {
        // ExmodFolder.Write always writes ".EXMOD" (uppercase) itself, so this exercises the
        // detection's own OrdinalIgnoreCase choice directly against a lowercase file a different
        // tool (or a manually-renamed file) could plausibly produce.
        File.WriteAllText(Path.Combine(_extractedDir, "something.exmod"), "{}");
        var libraryRepository = new FakeLibraryRepository();

        var (_, _, kind, _) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.Equal(1, libraryRepository.ImportCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ExmodValidationThrows_PropagatesRatherThanFallingBackToAnotherKind()
    {
        // Genuinely EXMOD-shaped content that fails validation (corrupt JSON, an unsafe asset path,
        // more than one .EXMOD) is a real problem with a real EXMOD import — not a signal to
        // silently reclassify it as a pak or a UE4SS mod instead.
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        var libraryRepository = new FakeLibraryRepository { ImportException = new FormatException("corrupt EXMOD") };

        await Assert.ThrowsAsync<FormatException>(() => ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null));
    }

    [Fact]
    public async Task ClassifyAndImport_SingleBarePakNoExmod_ImportsThroughPrebuiltPakImporter()
    {
        var pakPath = Path.Combine(_extractedDir, "MyMod.pak");
        File.WriteAllBytes(pakPath, [1, 2, 3, 4]);
        var prebuiltPakImporter = new FakePrebuiltPakImporter
        {
            Result = new LibraryEntry
            {
                FolderName = "MyMod", Name = "MyMod", Author = "Unknown", Version = "1.0",
                Description = "", FileName = "MyMod", IsOpaquePak = true,
            },
        };

        var (entryName, folderName, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "MyMod.zip", new FakeLibraryRepository(), new FakeUe4ssModRepository(), prebuiltPakImporter, "SomeDataFolder", "SomeUnrealPak.exe");

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.True(isOpaquePak);
        Assert.Equal("MyMod", entryName);
        Assert.Equal("MyMod", folderName);
        Assert.Equal(1, prebuiltPakImporter.ImportCallCount);
        Assert.Equal(pakPath, prebuiltPakImporter.LastPakFilePath);
    }

    [Fact]
    public async Task ClassifyAndImport_PakConvertsToARealExmod_IsOpaquePakReflectsWhatTheImporterReturned()
    {
        // Kind alone can't distinguish "a real EXMOD" from "a pak that converted into one" — both
        // are PendingDownloadActivationKind.Library — only IsOpaquePak does, and it must come
        // straight from IPrebuiltPakImporter's own result, not be hardcoded true just because a
        // .pak file was what triggered this branch.
        File.WriteAllBytes(Path.Combine(_extractedDir, "MyMod.pak"), [1, 2, 3, 4]);
        var prebuiltPakImporter = new FakePrebuiltPakImporter
        {
            Result = new LibraryEntry
            {
                FolderName = "MyMod", Name = "MyMod", Author = "Unknown", Version = "1.0",
                Description = "", FileName = "MyMod", IsOpaquePak = false,
            },
        };

        var (_, _, _, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "MyMod.zip", new FakeLibraryRepository(), new FakeUe4ssModRepository(), prebuiltPakImporter, "SomeDataFolder", "SomeUnrealPak.exe");

        Assert.False(isOpaquePak);
    }

    [Fact]
    public async Task ClassifyAndImport_MultiplePaksNoExmod_ThrowsInsteadOfGuessingWhichOneToImport()
    {
        File.WriteAllBytes(Path.Combine(_extractedDir, "First.pak"), [1]);
        File.WriteAllBytes(Path.Combine(_extractedDir, "Second.pak"), [2]);
        var prebuiltPakImporter = new FakePrebuiltPakImporter();

        var ex = await Assert.ThrowsAsync<FormatException>(() => ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Ambiguous.zip", new FakeLibraryRepository(), new FakeUe4ssModRepository(), prebuiltPakImporter, "SomeDataFolder", null));

        Assert.Contains("2", ex.Message);
        Assert.Equal(0, prebuiltPakImporter.ImportCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ExmodAndABarePakBothPresent_ExmodTakesPriorityOverThePak()
    {
        // A real mod could plausibly ship both — the .EXMOD is what actually carries this mod's own
        // declared identity/content, so it must win outright rather than the ambiguous-pak-count
        // check (or the single-pak import) ever getting a look at the .pak sitting alongside it.
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        File.WriteAllBytes(Path.Combine(_extractedDir, "Bundled.pak"), [1, 2, 3]);
        var libraryRepository = new FakeLibraryRepository();
        var prebuiltPakImporter = new FakePrebuiltPakImporter();

        var (_, _, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), prebuiltPakImporter, "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.False(isOpaquePak);
        Assert.Equal(1, libraryRepository.ImportCallCount);
        Assert.Equal(0, prebuiltPakImporter.ImportCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_NeitherExmodNorPak_FallsBackToAUe4ssModImport()
    {
        // The one kind this app handles that carries no metadata file of its own to detect it by —
        // a real UE4SS mod's own shape is just Lua scripts/assets under an arbitrary folder name.
        var scriptsDir = Path.Combine(_extractedDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "main.lua"), "print('hello')");
        var ue4ssModRepository = new FakeUe4ssModRepository { Result = "CoolMod_1" };

        var (entryName, folderName, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "CoolMod.zip", new FakeLibraryRepository(), ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Ue4ssMod, kind);
        Assert.False(isOpaquePak);
        Assert.Equal("CoolMod_1", entryName);
        Assert.Equal("CoolMod_1", folderName);
        Assert.Equal(1, ue4ssModRepository.ImportFromFolderCallCount);
        Assert.Equal(_extractedDir, ue4ssModRepository.LastSourceFolder);
        // The archive's own original filename (sans extension), not some path derived from the
        // extracted temp directory — that temp folder name is a random Guid, meaningless to a user.
        Assert.Equal("CoolMod", ue4ssModRepository.LastFallbackName);
    }

    [Fact]
    public async Task ClassifyAndImport_EmptyExtractedFolder_StillFallsBackToUe4ssModImport()
    {
        // An edge case worth pinning down explicitly: nothing recognizable at all still resolves to
        // SOME outcome (the UE4SS catch-all) rather than throwing — ImportOnePath's caller has
        // nothing better to fall back to either.
        var ue4ssModRepository = new FakeUe4ssModRepository { Result = "Empty_1" };

        var (_, _, kind, _) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Empty.zip", new FakeLibraryRepository(), ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Ue4ssMod, kind);
        Assert.Equal(1, ue4ssModRepository.ImportFromFolderCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ProvenanceTags_FlowThroughToLibraryImport()
    {
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        var libraryRepository = new FakeLibraryRepository();

        await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(),
            "SomeDataFolder", null, source: "Nexus", nexusModId: 42);

        Assert.Equal("Nexus", libraryRepository.LastImportSource);
        Assert.Equal(42, libraryRepository.LastImportNexusModId);
    }

    private sealed class FakeLibraryRepository : ILibraryRepository
    {
        public Exception? ImportException { get; set; }
        public int ImportCallCount { get; private set; }
        public string? LastImportSourcePath { get; private set; }
        public string? LastImportSource { get; private set; }
        public int? LastImportNexusModId { get; private set; }

        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
        {
            ImportCallCount++;
            LastImportSourcePath = sourcePath;
            LastImportSource = source;
            LastImportNexusModId = nexusModId;
            if (ImportException is not null)
            {
                throw ImportException;
            }

            return new LibraryEntry
            {
                FolderName = "Faster_Processors", Name = "Faster Processors", Author = "Someone",
                Version = "1.0", Description = "D", FileName = "Faster_Processors",
            };
        }

        public IReadOnlyList<LibraryEntry> GetAll() => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> UnreadableFolders => [];
        public IReadOnlyList<LibraryEntry> Search(string query) => throw new NotSupportedException("Not exercised by these tests.");
        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null) =>
            throw new NotSupportedException("Not exercised by these tests — ExtractedModClassifier goes through IPrebuiltPakImporter, not this directly.");
        public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void Refresh() => throw new NotSupportedException("Not exercised by these tests.");
        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void MarkLocallyEdited(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void MarkConvertedFromPrebuiltPak(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void SetDisplayNameOverride(string folderName, string? displayName) => throw new NotSupportedException("Not exercised by these tests.");
        public void LinkToNexus(string folderName, int nexusModId) => throw new NotSupportedException("Not exercised by these tests.");
        public void SetCatalogEntry(string folderName, string catalogEntryId) => throw new NotSupportedException("Not exercised by these tests.");
        public string BackupMod(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public bool HasModBackup(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public bool RestoreLatestModBackup(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string? TryGetLatestModBackupPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public LibraryEntry CreateBlankMod(string name, string author, ModTemplate template = ModTemplate.Blank) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListAssetPaths(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListAssetPaths(string folderName, IReadOnlyList<string> precomputedFiles) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public byte[] ReadAssetContent(string folderName, string relativePath) => throw new NotSupportedException("Not exercised by these tests.");
        public string? ReadReadme(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string? ReadReadme(string folderName, IReadOnlyList<string> precomputedFiles) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListFolderFiles(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUe4ssModRepository : IUe4ssModRepository
    {
        public string Result { get; set; } = "SomeMod_1";
        public int ImportFromFolderCallCount { get; private set; }
        public string? LastSourceFolder { get; private set; }
        public string? LastFallbackName { get; private set; }

        public string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null)
        {
            ImportFromFolderCallCount++;
            LastSourceFolder = sourceFolder;
            LastFallbackName = fallbackName;
            return Result;
        }

        public IReadOnlyList<string> GetAll() => throw new NotSupportedException("Not exercised by these tests.");
        public string Import(string zipFilePath, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");
        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) => throw new NotSupportedException("Not exercised by these tests.");
        public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakePrebuiltPakImporter : IPrebuiltPakImporter
    {
        public LibraryEntry Result { get; set; } = new()
        {
            FolderName = "MyMod", Name = "MyMod", Author = "Unknown", Version = "1.0", Description = "", FileName = "MyMod", IsOpaquePak = true,
        };

        public int ImportCallCount { get; private set; }
        public string? LastPakFilePath { get; private set; }

        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default)
        {
            ImportCallCount++;
            LastPakFilePath = pakFilePath;
            return Task.FromResult(Result);
        }
    }
}
