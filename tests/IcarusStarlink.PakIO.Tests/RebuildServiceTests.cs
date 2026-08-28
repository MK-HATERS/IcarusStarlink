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

        // Real IReadOnlyDictionary keyed by pak file path -> the fake "contents" to drop into
        // outputDirectory when that pak is "extracted" — a real UnrealPak.exe isn't available in a
        // unit test, so this stands in for it the same way FakeProcessRunner does elsewhere.
        // ListPakContentsAsync (RebuildService's own pre-extraction collision check) reads the same
        // map's keys, matching what a real -List call would report before anything is written.
        public Dictionary<string, Dictionary<string, string>> FakeExtractedFilesByPakPath { get; } = [];

        public Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                FakeExtractedFilesByPakPath.TryGetValue(pakFilePath, out var files) ? [.. files.Keys] : []);
        public List<string> ExtractedPakPaths { get; } = [];

        public Task<int> ExtractPakAsync(string unrealPakExePath, string pakFilePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            ExtractedPakPaths.Add(pakFilePath);
            // Matches the real UnrealPakService.ExtractPakAsync, which always creates
            // outputDirectory before running the process regardless of how many files the pak
            // actually contains — RebuildService's own prebuilt-pak scratch-extraction step relies
            // on the directory existing afterward even for a pak with nothing registered here.
            Directory.CreateDirectory(outputDirectory);
            if (!FakeExtractedFilesByPakPath.TryGetValue(pakFilePath, out var files))
            {
                return Task.FromResult(0);
            }

            foreach (var (relativePath, content) in files)
            {
                var path = Path.Combine(outputDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            return Task.FromResult(files.Count);
        }

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

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

        Assert.Equal(1, result.MergedFileCount);
        var stagedJson = pakService.StagedFileContentsAtCallTime["data/Crafting/D_ProcessorRecipes.json"];
        Assert.Contains("\"CraftTime\": 1", stagedJson);
    }

    [Fact]
    public async Task RebuildAsync_ReportsProgressThroughToCompletion()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Faster Crafting", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);
        var reported = new List<RebuildStageProgress>();

        await service.RebuildAsync(
            [mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, [], progress: new Progress<RebuildStageProgress>(reported.Add));

        // Progress<T> marshals through SynchronizationContext.Post, which without a real UI message
        // loop (as here, in a test) runs synchronously — still, give it a moment so a flush isn't
        // required for this assertion to be meaningful either way.
        Assert.NotEmpty(reported);
        Assert.Equal(0, reported[0].PercentComplete);
        Assert.Equal(100, reported[^1].PercentComplete);
        Assert.True(reported.Select(p => p.PercentComplete).SequenceEqual(reported.Select(p => p.PercentComplete).OrderBy(p => p)),
            "Percentages should never go backwards across the pipeline.");
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
        await service.RebuildAsync([modA, modB], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

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

        await service.RebuildAsync([modA, modB], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, [], manualPicks);

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

        var result = await service.RebuildAsync([modA, modB], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

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

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

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

        await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

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

        var result = await service.RebuildAsync([modA, modB], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

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

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

        // 1 merged data file + 1 asset file = 2 staged files.
        Assert.Equal(2, result.PackedFileCount);
    }

    [Fact]
    public async Task RebuildAsync_AttachedPrebuiltPak_ItsContentsAreFoldedIntoTheSameStaging()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Mod With Asset", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) },
            assets: [new ExmodAssetEntry("BP/Building/BP_Thing.uasset", [1, 2, 3])]);
        var prebuiltPakPath = Path.Combine(_tempDir, "SomeMod_P.pak");
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFilesByPakPath[prebuiltPakPath] = new() { ["Prebuilt/Thing.uasset"] = "prebuilt bytes" };
        var service = new RebuildService(pakService);

        await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, [prebuiltPakPath]);

        // Both the queued EXMOD mod's own asset and the attached prebuilt pak's own extracted
        // content land in the exact same staging folder that CreatePakAsync packs — one final pak,
        // not two separate files.
        Assert.Contains(prebuiltPakPath, pakService.ExtractedPakPaths);
        Assert.Contains("BP/Building/BP_Thing.uasset", pakService.StagedRelativePathsAtCallTime);
        Assert.Contains("Prebuilt/Thing.uasset", pakService.StagedRelativePathsAtCallTime);
    }

    [Fact]
    public async Task RebuildAsync_PrebuiltPakOverwritesAFieldMergedPath_AddsWarning()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Mod A", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var prebuiltPakPath = Path.Combine(_tempDir, "SomeMod_P.pak");
        var pakService = new FakeUnrealPakService();
        // Collides with the exact path StageMergedTables writes the field-merged table to.
        pakService.FakeExtractedFilesByPakPath[prebuiltPakPath] = new() { ["data/Crafting/D_ProcessorRecipes.json"] = "raw overwrite" };
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, [prebuiltPakPath]);

        Assert.Contains(result.Warnings, w => w.Contains("SomeMod_P") && w.Contains("data/Crafting/D_ProcessorRecipes.json"));
        // The prebuilt pak's own raw bytes still win on disk — the warning discloses the loss, it
        // doesn't prevent it (there's genuinely no way to reconcile raw bytes against a merge).
        var stagedJson = pakService.StagedFileContentsAtCallTime["data/Crafting/D_ProcessorRecipes.json"];
        Assert.Equal("raw overwrite", stagedJson);
    }

    [Fact]
    public async Task RebuildAsync_PrebuiltPakWithNoCollisions_NoWarning()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Mod A", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var prebuiltPakPath = Path.Combine(_tempDir, "SomeMod_P.pak");
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFilesByPakPath[prebuiltPakPath] = new() { ["Prebuilt/Thing.uasset"] = "prebuilt bytes" };
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, [prebuiltPakPath]);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task RebuildAsync_PrebuiltPakAndModTouchDifferentFieldsOfSameFile_BothSurviveTheMerge()
    {
        // The bug this proves fixed: a prebuilt pak used to be extracted as raw bytes straight into
        // staging AFTER the merge output was written, so it unconditionally replaced the WHOLE FILE
        // on any path collision — silently discarding a field-merged mod's own changes to that same
        // file even when the two never actually touched the same field. Now the prebuilt pak's own
        // DataTable JSON is diffed against base data into real FieldChanges first, so it becomes a
        // genuine MergeEngine participant and only the field it actually changed is affected.
        WriteBaseTable("Crafting/D_ProcessorRecipes.json",
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5,"RequiredMillijoules":100}]}""");
        var mod = MakeMod("Mod A", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["RequiredMillijoules"] = System.Text.Json.Nodes.JsonValue.Create(77) });
        var prebuiltPakPath = Path.Combine(_tempDir, "SomeMod_P.pak");
        var pakService = new FakeUnrealPakService();
        // Only CraftTime actually differs from base here — RequiredMillijoules matches base, so it
        // isn't a real change on the prebuilt pak's side and never competes with the mod's own 77.
        pakService.FakeExtractedFilesByPakPath[prebuiltPakPath] = new()
        {
            ["data/Crafting/D_ProcessorRecipes.json"] =
                """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":999,"RequiredMillijoules":100}]}""",
        };
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, [prebuiltPakPath]);

        Assert.Empty(result.Warnings);
        var stagedJson = pakService.StagedFileContentsAtCallTime["data/Crafting/D_ProcessorRecipes.json"];
        var row = System.Text.Json.Nodes.JsonNode.Parse(stagedJson)!["Rows"]![0]!;
        Assert.Equal(999, (int)row["CraftTime"]!);
        Assert.Equal(77, (int)row["RequiredMillijoules"]!);
    }

    [Fact]
    public async Task RebuildAsync_PrebuiltPakAndModTouchSameField_ResolvesViaLastModWinsNotWholeFileClobber()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json",
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5,"RequiredMillijoules":100}]}""");
        var mod = MakeMod("Mod A", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new()
            {
                ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1),
                ["RequiredMillijoules"] = System.Text.Json.Nodes.JsonValue.Create(77),
            });
        var prebuiltPakPath = Path.Combine(_tempDir, "SomeMod_P.pak");
        var pakService = new FakeUnrealPakService();
        pakService.FakeExtractedFilesByPakPath[prebuiltPakPath] = new()
        {
            ["data/Crafting/D_ProcessorRecipes.json"] =
                """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":999,"RequiredMillijoules":100}]}""",
        };
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, [prebuiltPakPath]);

        // A genuine field-level conflict (CraftTime: mod wants 1, prebuilt pak wants 999) resolves
        // via MergeEngine's normal last-mod-wins default (the prebuilt pak, appended after the
        // queued mod) — not disclosed as an unresolvable "no way to reconcile raw bytes" warning.
        Assert.Empty(result.Warnings);
        var stagedJson = pakService.StagedFileContentsAtCallTime["data/Crafting/D_ProcessorRecipes.json"];
        var row = System.Text.Json.Nodes.JsonNode.Parse(stagedJson)!["Rows"]![0]!;
        Assert.Equal(999, (int)row["CraftTime"]!);
        // Proves this was a real field-level resolution, not the old whole-file overwrite: the
        // mod's own RequiredMillijoules change survives even though the prebuilt pak "won" CraftTime
        // — the old bug would have silently reverted this back to the prebuilt pak's base-matching
        // 100 by replacing the entire file's raw bytes.
        Assert.Equal(77, (int)row["RequiredMillijoules"]!);
    }

    [Fact]
    public async Task RebuildAsync_AttachedPrebuiltPak_NameIsListedInTheManifest()
    {
        var prebuiltPakPath = Path.Combine(_tempDir, "SomeMod_P.pak");
        var service = new RebuildService(new FakeUnrealPakService());

        var result = await service.RebuildAsync([], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, [prebuiltPakPath]);

        var manifest = await File.ReadAllTextAsync(result.ManifestPath);
        Assert.Contains("SomeMod_P", manifest);
    }

    [Fact]
    public async Task RebuildAsync_QueuedModAlsoTouchesStatsGranted_BuiltInGameplayOptionStillWinsByDefault()
    {
        // Phase 1 of the EXMOD/merge-options plan made Speed/Player/XP Boost and Disable
        // Temperatures real FieldChanges instead of a separate post-merge pass — this proves the
        // "built-in wins" default this always had is unchanged (a genuine no-regression check), even
        // though the conflict is now visible to MergeEngine.FindConflicts (see
        // GameplayOptionsFieldChangeGeneratorTests for that half).
        WriteBaseTable("Stats/D_CharacterStartingStats.json",
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Base_Stats","StatsGranted":{}}]}""");
        var mod = MakeMod("Speed Mod", "Stats-D_CharacterStartingStats.json", "Base_Stats",
            new() { ["StatsGranted"] = new System.Text.Json.Nodes.JsonObject { ["(Value=\"BaseMovementSpeed_+\")"] = 999 } });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        await service.RebuildAsync(
            [mod], new GameplayOptions { SpeedBoost = BoostLevel.Level1 }, _dataFolder, _unrealPakExePath, _outputPakPath, []);

        var stagedJson = pakService.StagedFileContentsAtCallTime["data/Stats/D_CharacterStartingStats.json"];
        var stagedNode = System.Text.Json.Nodes.JsonNode.Parse(stagedJson)!;
        var statsGranted = stagedNode["Rows"]![0]!["StatsGranted"]!.AsObject();
        // The built-in Speed Boost's own real documented value (Level 1 = 455), not the queued
        // mod's 999 — same default this had before Phase 1, just now a real, visible conflict
        // resolution instead of a silent post-merge overwrite.
        Assert.Equal(455, (int)statsGranted["(Value=\"BaseMovementSpeed_+\")"]!);
    }

    [Fact]
    public async Task RebuildAsync_CurrentFileEscapesDataFolder_SkipsWithWarningInsteadOfReadingOutsideDataFolder()
    {
        // CurrentFile is untrusted EXMOD content (arrives via a shared/downloaded mod) — a
        // "../"-laden value must never let this read (or, via StageMergedTables, write) a file
        // outside the extracted game data folder / staging directory.
        var secretPath = Path.Combine(_tempDir, "secret.json");
        File.WriteAllText(secretPath, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"TopSecret","Value":1}]}""");
        var mod = MakeMod("Malicious Mod", "..-secret.json", "TopSecret",
            new() { ["Value"] = System.Text.Json.Nodes.JsonValue.Create(999) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

        Assert.Equal(0, result.MergedFileCount);
        Assert.Contains(result.Warnings, w => w.Contains("..-secret.json") && w.Contains("valid location"));
        Assert.DoesNotContain(pakService.StagedRelativePathsAtCallTime, p => p.Contains("secret.json"));
    }

    [Fact]
    public async Task RebuildAsync_CurrentFileIsRootedAbsolutePath_SkipsWithWarningInsteadOfReadingArbitraryFile()
    {
        var secretPath = Path.Combine(_tempDir, "secret2.json");
        File.WriteAllText(secretPath, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"TopSecret2","Value":1}]}""");
        var mod = MakeMod("Malicious Mod 2", secretPath.Replace('\\', '/'), "TopSecret2",
            new() { ["Value"] = System.Text.Json.Nodes.JsonValue.Create(999) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        var result = await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

        Assert.Equal(0, result.MergedFileCount);
        Assert.Contains(result.Warnings, w => w.Contains("valid location"));
        Assert.DoesNotContain(pakService.StagedRelativePathsAtCallTime, p => p.Contains("secret2.json"));
    }

    [Fact]
    public async Task RebuildAsync_CleansUpItsOwnStagingDirectory()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
        var mod = MakeMod("Mod", "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(1) });
        var pakService = new FakeUnrealPakService();
        var service = new RebuildService(pakService);

        await service.RebuildAsync([mod], new GameplayOptions(), _dataFolder, _unrealPakExePath, _outputPakPath, []);

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
