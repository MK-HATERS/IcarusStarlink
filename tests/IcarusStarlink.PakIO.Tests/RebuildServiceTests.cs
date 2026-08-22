using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Pak;
using IcarusStarlink.PakIO.Rebuild;

namespace IcarusStarlink.PakIO.Tests;

public class RebuildServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _dataFolder;
    private readonly string _unrealPakExePath;
    private readonly string _outputPakPath;

    public RebuildServiceTests()
    {
        _dataFolder = Path.Combine(_tempDir, "Data");
        _unrealPakExePath = Path.Combine(_tempDir, "UnrealPak.exe");
        _outputPakPath = Path.Combine(_tempDir, "Staged_Build", "ISL-Merged_P.pak");

        Directory.CreateDirectory(_dataFolder);
        File.WriteAllText(_unrealPakExePath, "fake exe bytes");
    }

    private void WriteBaseTable(string relativePath, string json)
    {
        var path = Path.Combine(_dataFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static ExmodPackageContents MakeMod(
        string name, string currentFile, string itemName, Dictionary<string, System.Text.Json.Nodes.JsonNode?> fields,
        IReadOnlyList<ExmodAssetEntry>? assets = null) => new(
        new ExmodPackage
        {
            Name = name,
            Author = "TestAuthor",
            Version = "1.0",
            Description = "d",
            FileName = name.Replace(' ', '_'),
            Rows = [new ExmodFileRow { CurrentFile = currentFile, FileItems = [new ExmodFileItem { Name = itemName, Fields = fields }] }],
        },
        assets ?? []);

    private sealed class FakeUnrealPakService : IUnrealPakService
    {
        public string? LastStagingDirectory { get; private set; }
        public string? LastOutputPakPath { get; private set; }
        public List<string> StagedRelativePathsAtCallTime { get; private set; } = [];

        public Task<UnrealPakExtractResult> ExtractDataPakAsync(
            string unrealPakExePath, string icarusContentPath, string outputDirectory, DateTimeOffset? previousUpdateAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> TryGetDataPakHashAsync(string icarusContentPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        // RebuildAsync deletes its own staging directory in a finally block before returning, so
        // both the file list and each file's content have to be captured here, while this call
        // is still executing — nothing after RebuildAsync returns can see the staging dir at all.
        public Dictionary<string, string> StagedFileContentsAtCallTime { get; private set; } = [];

        public Task<int> CreatePakAsync(string unrealPakExePath, string stagingDirectory, string outputPakPath, CancellationToken cancellationToken = default)
        {
            LastStagingDirectory = stagingDirectory;
            LastOutputPakPath = outputPakPath;
            var files = Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories);
            StagedRelativePathsAtCallTime = [.. files.Select(f => Path.GetRelativePath(stagingDirectory, f).Replace('\\', '/'))];
            StagedFileContentsAtCallTime = files.ToDictionary(
                f => Path.GetRelativePath(stagingDirectory, f).Replace('\\', '/'),
                File.ReadAllText);
            return Task.FromResult(StagedRelativePathsAtCallTime.Count);
        }
    }

    [Fact]
    public async Task RebuildAsync_SingleModSingleFieldChange_StagesMergedFileWithNewValue()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Faster Crafting", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath);

        Assert.Equal(1, result.MergedFileCount);
        var stagedJson = pakService.StagedFileContentsAtCallTime["data/Crafting/D_ProcessorRecipes.json"];
        Assert.Contains("\"CraftTime\": 1", stagedJson);
    }

    [Fact]
    public async Task RebuildAsync_TwoModsSameField_LaterModInQueueWins()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var modA = MakeMod("Mod A", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var modB = MakeMod("Mod B", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(2) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        // modB is later in the queue (higher priority) — matches MergeEngine's own
        // "index 0 = lowest priority" convention.
        await service.RebuildAsync([modA, modB], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath);

        var stagedJson = pakService.StagedFileContentsAtCallTime["data/Crafting/D_ProcessorRecipes.json"];
        Assert.Contains("\"CraftTime\": 2", stagedJson);
    }

    [Fact]
    public async Task RebuildAsync_ManualPickOverridesLastWriteWins()
    {
        // Same conflict as RebuildAsync_TwoModsSameField_LaterModInQueueWins, but this time the
        // advanced conflict picker explicitly picks the earlier mod (index 0) — the whole point of
        // manualPicks is overriding what the registry's default rule would otherwise do.
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var modA = MakeMod("Mod A", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var modB = MakeMod("Mod B", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(2) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);
        var manualPicks = new Dictionary<(string, string, string), int>
        {
            [("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe", "CraftTime")] = 0, // modA, not the last
        };

        await service.RebuildAsync([modA, modB], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, manualPicks);

        var stagedJson = pakService.StagedFileContentsAtCallTime["data/Crafting/D_ProcessorRecipes.json"];
        Assert.Contains("\"CraftTime\": 1", stagedJson);
    }

    [Fact]
    public async Task RebuildAsync_TwoModsSameFieldDifferentCurrentFileCasing_LaterModStillWinsInsteadOfBothWritingSeparately()
    {
        // Different EXMOD authors' extraction tools aren't guaranteed to emit CurrentFile with
        // consistent casing for the exact same real (case-insensitive) Windows path — modA and
        // modB must still be treated as touching the same file and conflict-resolved together,
        // not staged as two separate writes to the same physical destination.
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var modA = MakeMod("Mod A", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var modB = MakeMod("Mod B", "crafting-d_processorrecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(2) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([modA, modB], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath);

        // Exactly one staged file for the pair (not two, one per casing variant) — the winning
        // group's own CurrentFile casing (whichever mod's it happens to be) decides the on-disk
        // output filename's casing, which Windows itself doesn't care about, so this looks it up
        // case-insensitively rather than assuming which one survived.
        Assert.Equal(1, result.MergedFileCount);
        var stagedEntry = Assert.Single(pakService.StagedFileContentsAtCallTime, kv =>
            kv.Key.Equals("data/Crafting/D_ProcessorRecipes.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"CraftTime\": 2", stagedEntry.Value);
    }

    [Fact]
    public async Task RebuildAsync_CurrentFileWithNoMatchingBaseData_AddsWarningNotThrow()
    {
        var mod = MakeMod("Ghost Mod", "NoSuchCategory-D_Missing.json", "Item",
            new() { ["X"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath);

        Assert.Equal(0, result.MergedFileCount);
        Assert.Contains(result.Warnings, w => w.Contains("NoSuchCategory-D_Missing.json"));
    }

    [Fact]
    public async Task RebuildAsync_ModAssets_StagedAtTheirOwnRelativePath()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Mod With Asset", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) },
            assets: [new ExmodAssetEntry("BP/Building/BP_Thing.uasset", [1, 2, 3])]);
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath);

        Assert.Contains("BP/Building/BP_Thing.uasset", pakService.StagedRelativePathsAtCallTime);
    }

    [Fact]
    public async Task RebuildAsync_WritesManifestListingEachModName()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var modA = MakeMod("First Mod", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var modB = MakeMod("Second Mod", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(2) });
        var service = new RebuildService(new FakeUnrealPakService());

        var result = await service.RebuildAsync([modA, modB], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath);

        var manifest = await File.ReadAllTextAsync(result.ManifestPath);
        Assert.Contains("Includes the following mods:", manifest);
        Assert.Contains("First Mod", manifest);
        Assert.Contains("Second Mod", manifest);
    }

    [Fact]
    public async Task RebuildAsync_ReturnsPackedFileCountFromUnrealPakService()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Mod", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) },
            assets: [new ExmodAssetEntry("BP/Thing.uasset", [1])]);
        var service = new RebuildService(new FakeUnrealPakService());

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath);

        // 1 merged data file + 1 asset file = 2 staged files.
        Assert.Equal(2, result.PackedFileCount);
    }

    [Fact]
    public async Task RebuildAsync_CleansUpItsOwnStagingDirectory()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Mod", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath);

        Assert.False(Directory.Exists(pakService.LastStagingDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
