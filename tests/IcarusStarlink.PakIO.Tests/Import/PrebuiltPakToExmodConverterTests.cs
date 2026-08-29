using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests.Import;

public class PrebuiltPakToExmodConverterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _dataFolder;
    private readonly string _unrealPakExePath;
    private readonly string _pakFilePath;

    public PrebuiltPakToExmodConverterTests()
    {
        _dataFolder = Path.Combine(_tempDir, "Data");
        _unrealPakExePath = Path.Combine(_tempDir, "UnrealPak.exe");
        _pakFilePath = Path.Combine(_tempDir, "SomeMod_P.pak");

        Directory.CreateDirectory(_dataFolder);
        File.WriteAllText(_unrealPakExePath, "fake exe bytes");
    }

    private void WriteBaseTable(string relativePath, string json)
    {
        var path = Path.Combine(_dataFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>Same fake-instead-of-a-real-UnrealPak.exe pattern RebuildServiceTests already established — extraction just drops pre-registered fake file contents into outputDirectory.</summary>
    private sealed class FakeUnrealPakService : IUnrealPakService
    {
        public Dictionary<string, string> FakeExtractedFiles { get; } = [];
        public Exception? ThrowOnExtract { get; set; }
        public List<string> ExtractedPakPaths { get; } = [];
        public string? LastScratchDirectory { get; private set; }

        public Task<int> ExtractPakAsync(string unrealPakExePath, string pakFilePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            ExtractedPakPaths.Add(pakFilePath);
            LastScratchDirectory = outputDirectory;
            if (ThrowOnExtract is not null)
            {
                throw ThrowOnExtract;
            }

            Directory.CreateDirectory(outputDirectory);
            foreach (var (relativePath, content) in FakeExtractedFiles)
            {
                var path = Path.Combine(outputDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            return Task.FromResult(FakeExtractedFiles.Count);
        }

        public Task<UnrealPakExtractResult> ExtractDataPakAsync(
            string unrealPakExePath, string icarusContentPath, string outputDirectory, DateTimeOffset? previousUpdateAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> TryGetDataPakHashAsync(string icarusContentPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CreatePakAsync(string unrealPakExePath, string stagingDirectory, string outputPakPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task TryConvertAsync_UnrealPakExeMissing_ReturnsNullWithWarning()
    {
        var report = new MergeReport();
        var converter = new PrebuiltPakToExmodConverter(new FakeUnrealPakService());

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, Path.Combine(_tempDir, "NoSuchExe.exe"), "Some Mod", "Someone", report);

        Assert.Null(result);
        Assert.Contains(report.Warnings, w => w.Contains("UnrealPak.exe"));
    }

    [Fact]
    public async Task TryConvertAsync_DataFolderNeverExtracted_ReturnsNullWithWarning()
    {
        var emptyDataFolder = Path.Combine(_tempDir, "EmptyData");
        Directory.CreateDirectory(emptyDataFolder);
        var report = new MergeReport();
        var converter = new PrebuiltPakToExmodConverter(new FakeUnrealPakService());

        var result = await converter.TryConvertAsync(_pakFilePath, emptyDataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.Null(result);
        Assert.Contains(report.Warnings, w => w.Contains("Update data folder"));
    }

    [Fact]
    public async Task TryConvertAsync_ExtractionThrows_ReturnsNullWithWarningInsteadOfPropagating()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService { ThrowOnExtract = new InvalidOperationException("UnrealPak.exe exited with code 1") };
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.Null(result);
        Assert.Contains(report.Warnings, w => w.Contains("SomeMod_P") && w.Contains("exited with code 1"));
    }

    [Fact]
    public async Task TryConvertAsync_FieldDiffersFromBase_ProducesARealFieldLevelRow()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["data/Crafting/D_ProcessorRecipes.json"] =
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":1}]}""";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(result);
        // A diffed conversion's Name/Author are generic caller-supplied placeholders, not
        // author-declared — callers must be able to tell so they know it's safe to let a later
        // Nexus/Database link overwrite them (see IPrebuiltPakImporter's own use of this flag).
        Assert.False(result.HasAuthorDeclaredMetadata);
        var row = Assert.Single(result.Contents.Package.Rows);
        Assert.Equal("Crafting-D_ProcessorRecipes.json", row.CurrentFile);
        var item = Assert.Single(row.FileItems);
        Assert.Equal("Stone_Pickaxe", item.Name);
        var field = Assert.Single(item.Fields);
        Assert.Equal("CraftTime", field.Key);
        Assert.Equal(1, field.Value!.GetValue<int>());
        // The diffed data file is represented as a real field change above — it must not ALSO be
        // duplicated as a raw asset, or the two representations of the same content could drift.
        Assert.DoesNotContain(result.Contents.Assets, a => a.RelativePath == "data/Crafting/D_ProcessorRecipes.json");
    }

    [Fact]
    public async Task TryConvertAsync_NonDataFiles_BecomeAssetEntries()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["BP/Building/BP_Thing.uasset"] = "prebuilt bytes";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(result);
        Assert.Empty(result.Contents.Package.Rows);
        var asset = Assert.Single(result.Contents.Assets);
        Assert.Equal("BP/Building/BP_Thing.uasset", asset.RelativePath);
        Assert.Equal("prebuilt bytes", System.Text.Encoding.UTF8.GetString(asset.Content));
    }

    [Fact]
    public async Task TryConvertAsync_FileNameComesFromThePakItselfNotTheDisplayName()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["BP/Thing.uasset"] = "bytes";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "A Fancy Nexus Title!", "SomeAuthor", report);

        Assert.NotNull(result);
        Assert.Equal("SomeMod_P", result.Contents.Package.FileName);
        Assert.Equal("A Fancy Nexus Title!", result.Contents.Package.Name);
        Assert.Equal("SomeAuthor", result.Contents.Package.Author);
    }

    [Fact]
    public async Task TryConvertAsync_AlwaysCleansUpItsOwnScratchDirectoryAfterward()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["BP/Thing.uasset"] = "bytes";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(pakService.LastScratchDirectory);
        Assert.False(Directory.Exists(pakService.LastScratchDirectory));
    }

    [Fact]
    public async Task TryConvertAsync_DataFileWithNoMatchingBaseTable_FallsBackToRawAsset()
    {
        // Some unrelated base table exists (a real extracted Data folder always has hundreds of
        // files) — this pak's own D_NewThing.json just isn't one of them, adding wholly new
        // content the current game data has no counterpart for at all.
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["data/Crafting/D_NewThing.json"] = """{"RowStruct":"S","Defaults":{},"Rows":[]}""";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(result);
        Assert.Empty(result.Contents.Package.Rows);
        Assert.Contains(result.Contents.Assets, a => a.RelativePath == "data/Crafting/D_NewThing.json");
    }

    [Fact]
    public async Task TryConvertAsync_DataFileIsNotValidJson_FallsBackToRawAssetWithWarning()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["data/Crafting/D_ProcessorRecipes.json"] = "not valid json {{{";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(result);
        Assert.Empty(result.Contents.Package.Rows);
        Assert.Contains(result.Contents.Assets, a => a.RelativePath == "data/Crafting/D_ProcessorRecipes.json");
        Assert.Contains(report.Warnings, w => w.Contains("isn't valid JSON"));
    }

    /// <summary>Real content, byte-for-byte a real embedded EXMOD confirmed by extracting a real
    /// community-authored pak (BF_Shengong_Invincible_P.pak) — same header shape (legacy "ModName",
    /// "Level2"), same EndOfMod-with-no-File_Items convention, just with a placeholder field so
    /// tests don't depend on the specific stat values.</summary>
    private const string RealShapeEmbeddedExmod = """
        {
            "name": "Bundled_Mod_Name",
            "author": "Bundled Author",
            "version": "2.5",
            "fileName": "Bundled_Mod_Name",
            "description": "Bundled description straight from the author.",
            "ModName": "Some_Legacy_ModName",
            "Level2": "True",
            "Rows": [
                {
                    "CurrentFile": "Traits-D_Armour.json",
                    "File_Items": [
                        { "Name": "Undersuit_Shengong", "SomeField": 12345 }
                    ]
                },
                { "CurrentFile": "EndOfMod" }
            ]
        }
        """;

    [Fact]
    public async Task TryConvertAsync_PakHasBundledExmod_ReadsItDirectlyWithoutDiffing()
    {
        // No base table for "Traits-D_Armour.json" exists at all — a diff-based reconstruction
        // would find nothing to diff against and produce zero rows. Reading the bundled EXMOD
        // directly must still produce the real row regardless.
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["SomeMod_P.EXMOD"] = RealShapeEmbeddedExmod;
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(result);
        // The embedded EXMOD carries the real author's own declared metadata — callers must NOT
        // treat this like a diffed conversion's placeholder Name/Author (see
        // IPrebuiltPakImporter's own use of this flag to gate MarkConvertedFromPrebuiltPak).
        Assert.True(result.HasAuthorDeclaredMetadata);
        // The real sample this fixture is modeled on ends with the same "EndOfMod" sentinel row
        // every real EXMOD carries — reading the bundled file verbatim must preserve it, not just
        // the row(s) that actually change something.
        Assert.Equal(2, result.Contents.Package.Rows.Count);
        var row = Assert.Single(result.Contents.Package.Rows, r => r.CurrentFile == "Traits-D_Armour.json");
        var item = Assert.Single(row.FileItems);
        Assert.Equal("Undersuit_Shengong", item.Name);
        Assert.Equal(12345, item.Fields["SomeField"]!.GetValue<int>());
        Assert.Contains(result.Contents.Package.Rows, r => r.CurrentFile == "EndOfMod");
        Assert.Contains(report.Notes, n => n.Contains("bundled EXMOD data"));
    }

    [Fact]
    public async Task TryConvertAsync_PakHasBundledExmod_PrefersItsOwnMetadataOverCallerSupplied()
    {
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["SomeMod_P.EXMOD"] = RealShapeEmbeddedExmod;
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "A Fancy Nexus Title!", "SomeAuthor", report);

        Assert.NotNull(result);
        Assert.Equal("Bundled_Mod_Name", result.Contents.Package.Name);
        Assert.Equal("Bundled Author", result.Contents.Package.Author);
        Assert.Equal("2.5", result.Contents.Package.Version);
        Assert.Equal("Bundled description straight from the author.", result.Contents.Package.Description);
        // FileName is still always derived from the pak's own real filename, never from the
        // embedded package's own FileName — same rule as the caller-supplied `name` already has.
        Assert.Equal("SomeMod_P", result.Contents.Package.FileName);
    }

    [Fact]
    public async Task TryConvertAsync_PakHasBundledExmod_DataFolderIsNotDuplicatedAsAnAsset()
    {
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["SomeMod_P.EXMOD"] = RealShapeEmbeddedExmod;
        pakService.FakeExtractedFiles["data/Traits/D_Armour.json"] = """{"RowStruct":"S","Defaults":{},"Rows":[]}""";
        pakService.FakeExtractedFiles["BP/Thing.uasset"] = "real asset bytes";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(result);
        // The compiled "data/" table is fully superseded by the bundled EXMOD's own Rows — carrying
        // it through too would be redundant (and misleadingly look like a real bundled asset).
        Assert.DoesNotContain(result.Contents.Assets, a => a.RelativePath.StartsWith("data/"));
        // A genuine binary asset elsewhere in the pak must still come through normally.
        Assert.Contains(result.Contents.Assets, a => a.RelativePath == "BP/Thing.uasset");
        // The .EXMOD file itself was consumed as the package's own data, not a bundled asset.
        Assert.DoesNotContain(result.Contents.Assets, a => a.RelativePath == "SomeMod_P.EXMOD");
    }

    [Fact]
    public async Task TryConvertAsync_BundledExmodFileIsCorrupt_FallsBackToDiffingWithWarning()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["SomeMod_P.EXMOD"] = "not valid json {{{";
        pakService.FakeExtractedFiles["data/Crafting/D_ProcessorRecipes.json"] =
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":1}]}""";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(result);
        // Diffing still ran and found the real field change — the corrupt bundled file didn't
        // silently produce an empty mod, it fell all the way back to the existing working path.
        var row = Assert.Single(result.Contents.Package.Rows);
        Assert.Equal("Crafting-D_ProcessorRecipes.json", row.CurrentFile);
        Assert.Contains(report.Warnings, w => w.Contains("bundled EXMOD file"));
        Assert.DoesNotContain(report.Notes, n => n.Contains("bundled EXMOD data"));
    }

    [Fact]
    public async Task TryConvertAsync_ExmodFileNestedInASubfolder_IsNotTreatedAsBundled()
    {
        // Only a bare pak-root .EXMOD is the real convention (confirmed against
        // BF_Shengong_Invincible_P.pak) — one sitting inside some other subfolder is either
        // unrelated content or a coincidence, not classic IMM's own bundling convention.
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var report = new MergeReport();
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFiles["Nested/SomeMod_P.EXMOD"] = RealShapeEmbeddedExmod;
        pakService.FakeExtractedFiles["data/Crafting/D_ProcessorRecipes.json"] =
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":1}]}""";
        var converter = new PrebuiltPakToExmodConverter(pakService);

        var result = await converter.TryConvertAsync(_pakFilePath, _dataFolder, _unrealPakExePath, "Some Mod", "Someone", report);

        Assert.NotNull(result);
        var row = Assert.Single(result.Contents.Package.Rows);
        Assert.Equal("Crafting-D_ProcessorRecipes.json", row.CurrentFile);
        Assert.DoesNotContain(report.Notes, n => n.Contains("bundled EXMOD data"));
        // The nested file is neither bundled data nor a diffed table — it's carried through as an
        // ordinary asset, like any other file this conversion doesn't specifically understand.
        Assert.Contains(result.Contents.Assets, a => a.RelativePath == "Nested/SomeMod_P.EXMOD");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
