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
        var row = Assert.Single(result.Package.Rows);
        Assert.Equal("Crafting-D_ProcessorRecipes.json", row.CurrentFile);
        var item = Assert.Single(row.FileItems);
        Assert.Equal("Stone_Pickaxe", item.Name);
        var field = Assert.Single(item.Fields);
        Assert.Equal("CraftTime", field.Key);
        Assert.Equal(1, field.Value!.GetValue<int>());
        // The diffed data file is represented as a real field change above — it must not ALSO be
        // duplicated as a raw asset, or the two representations of the same content could drift.
        Assert.DoesNotContain(result.Assets, a => a.RelativePath == "data/Crafting/D_ProcessorRecipes.json");
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
        Assert.Empty(result.Package.Rows);
        var asset = Assert.Single(result.Assets);
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
        Assert.Equal("SomeMod_P", result.Package.FileName);
        Assert.Equal("A Fancy Nexus Title!", result.Package.Name);
        Assert.Equal("SomeAuthor", result.Package.Author);
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
        Assert.Empty(result.Package.Rows);
        Assert.Contains(result.Assets, a => a.RelativePath == "data/Crafting/D_NewThing.json");
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
        Assert.Empty(result.Package.Rows);
        Assert.Contains(result.Assets, a => a.RelativePath == "data/Crafting/D_ProcessorRecipes.json");
        Assert.Contains(report.Warnings, w => w.Contains("isn't valid JSON"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
