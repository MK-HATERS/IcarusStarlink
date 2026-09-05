using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Patches;
using IcarusStarlink.PakIO.Rebuild;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// MergeInstallViewModel had zero tests before this session despite being ~2000 lines of
/// safety-critical rebuild/install orchestration. Given its 13 constructor dependencies and the
/// real, hard limit that several of its own [RelayCommand] methods (ReviewConflictsAsync,
/// ReviewValidationIssuesAsync, SuggestQueueOrderAsync's own apply step, InstallAsync,
/// RemoveFromGameAsync) construct a real, modal, blocking WPF Window (ThemedMessageBox/
/// ConflictPickerWindow/ValidationIssueReportWindow) with no live Application in a headless test
/// host — this deliberately does NOT try to exercise those end-to-end. Instead it targets the four
/// highest-risk, currently-completely-unverified pieces called out for this pass, each through a
/// real, narrow, minimally-invasive internal seam added alongside these tests (see
/// IsRecomputeDebouncePending/RecomputeConflictCountAsync/RecomputeValidationIssueCountAsync/
/// ComputeSuggestedQueueOrderAsync/RebuildAsync's own doc comments on MergeInstallViewModel):
///
///   1. Queue mutation -&gt; the debounced conflict/validation recompute (does a mutation defer to
///      the debounce rather than compute inline, and does the recompute — once actually run —
///      produce the right answer for the current queue).
///   2. The per-folder package-fingerprint cache genuinely invalidating on a real file change and
///      genuinely NOT invalidating (serving the stale cached content) when the fingerprint itself
///      hasn't moved — proven together, since either test passing alone couldn't rule out "always
///      re-reads" or "never invalidates".
///   3. SuggestQueueOrderAsync's own reordering algorithm on a small, realistic mixed (EXMOD +
///      opaque pak) queue, deliberately using base-table data that gives the base-aware "real
///      changes vs base" signal a DIFFERENT answer than a naive raw-field-count would — so the
///      test can only pass if the real signal, not a simpler stand-in, is what's driving the sort.
///   4. RebuildAndInstallAsync's own top-level orchestration (Install only ever runs after a
///      Rebuild that actually succeeded; a guarded-out or throwing Rebuild never reaches Install at
///      all) against fakes for IRebuildService/IInstallService — no real UnrealPak.exe involved.
///
/// Every queued "mod" here is a real, on-disk EXMOD folder (ExmodJson.Parse + ExmodFolder.Write,
/// the same fixture technique ExmodFolderTests already established) rather than a hand-built
/// ExmodPackage in memory — LoadQueuedPackagesAsync always reads from disk via
/// ILibraryRepository.GetFolderPath + ExmodFolder.Read regardless of which other dependency is
/// faked, so a real folder is the only way to reach any of this ViewModel's own logic at all.
/// </summary>
public sealed class MergeInstallViewModelTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup only — never the point of these tests.
        }
    }

    private MergeInstallViewModel CreateViewModel(
        FakeLibraryRepository? libraryRepository = null,
        FakeRebuildService? rebuildService = null,
        FakeInstallService? installService = null,
        FakeProfileStore? profileStore = null,
        FakeSettingsService? settingsService = null)
    {
        settingsService ??= new FakeSettingsService();

        // RefreshHasExistingInstallAsync (constructor's own fire-and-forget background task) sets
        // CanCopyToGame from File.Exists(outputPakPath) independently of anything a test does —
        // pre-creating this empty file makes that background write agree with what a successful
        // RebuildAsync's own (synchronous, awaited) CanCopyToGame = true also sets, so a test
        // asserting CanCopyToGame can't flake depending on which one happens to run last.
        var outputPakPath = Path.Combine(_tempRoot, "Staged", "IcarusStarlink-Merged.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPakPath)!);
        File.WriteAllBytes(outputPakPath, []);

        return new MergeInstallViewModel(
            libraryRepository ?? new FakeLibraryRepository(),
            rebuildService ?? new FakeRebuildService(),
            installService ?? new FakeInstallService(),
            profileStore ?? new FakeProfileStore(),
            new FakePatchService(),
            new FakeDaedalusCatalogClient(),
            new FakeJimk72CatalogClient(),
            settingsService,
            new FakePakCompareService(),
            new PerformanceTracker(settingsService, Path.Combine(_tempRoot, "PerfLogs")),
            new FakeActivityLog(),
            // Deliberately a folder that doesn't exist — DataTableRowIndex.Build/BaseDataFileReader
            // both degrade to "nothing found" rather than throwing (matching SavesViewModelTests'
            // own "no real extracted game data" convention), which every test below relies on:
            // ExmodFieldValidityChecker/ExmodAssetCollisionChecker then contribute zero findings,
            // so ValidationIssueCount is driven ONLY by whatever ExmodReferenceChecker finds.
            Path.Combine(_tempRoot, "NoRealGameData"),
            outputPakPath,
            Path.Combine(_tempRoot, "Backups"));
    }

    private static LibraryEntry MakeEntry(string folderName, bool isOpaquePak = false) => new()
    {
        FolderName = folderName,
        Name = folderName,
        Author = "Test",
        Version = "1",
        Description = "D",
        FileName = folderName,
        IsOpaquePak = isOpaquePak,
    };

    /// <summary>Writes a real, on-disk EXMOD folder (ExmodFolder.Write, no assets) and returns its folder path plus the exact .EXMOD file path Write always uses — the same "Extracted Mods/&lt;FileName&gt;.EXMOD" convention ExmodFolder.Read expects back.</summary>
    private string CreateExmodFolder(string folderName, string exmodJson, out string exmodFilePath)
    {
        var package = ExmodJson.Parse(exmodJson);
        var folderPath = Path.Combine(_tempRoot, "Mods", folderName);
        ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
        exmodFilePath = Path.Combine(folderPath, "Extracted Mods", $"{package.FileName}.EXMOD");
        return folderPath;
    }

    private string CreateOpaquePakFolder(string folderName)
    {
        var folderPath = Path.Combine(_tempRoot, "Mods", folderName);
        Directory.CreateDirectory(folderPath);
        File.WriteAllBytes(Path.Combine(folderPath, "Prebuilt.pak"), [1, 2, 3, 4]);
        return folderPath;
    }

    /// <summary>Both mods declare the same file/item ("Crafting-D_ProcessorRecipes.json"/"SmelterRecipe") so two of them touching CraftTime with different values is a real, field-level FieldConflict per MergeEngine.FindConflicts — the same shape ReviewConflictsAsync's own badge would show.</summary>
    private static string BuildCraftTimeExmodJson(string fileName, int craftTime) => $$"""
        {
            "name": "{{fileName}}", "author": "Test", "version": "1", "description": "D",
            "fileName": "{{fileName}}",
            "Rows": [
                {"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                 "File_Items": [{"Name": "SmelterRecipe", "CraftTime": {{craftTime}}}]}
            ]
        }
        """;

    /// <summary>
    /// So that a queued mod editing "SmelterRecipe" resolves as an EDIT of an existing base item,
    /// not a brand-new one — real EXMOD-sourced FieldChanges always carry IsNewItem: true
    /// regardless (ExmodFieldChangeMapper's own doc comment), so without real base data like this,
    /// MergeEngine.FindNewItemNameCollisions would ALSO flag two mods that both touch
    /// "SmelterRecipe" as colliding on a brand-new item name, on top of the genuine field conflict
    /// the tests below are actually about — confirmed against FindNewItemNameCollisions' own doc
    /// comment ("baseTablesByFile matters MORE here... omitting it falls back to the raw IsNewItem
    /// flag"). The CraftTime value here (3) is deliberately neither 5 nor 99 (the two values these
    /// tests' own mods use) so GroupByField's own base-match filtering never removes either
    /// candidate — see its doc comment for why a candidate exactly matching base gets dropped.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonObject> BuildSmelterRecipeBaseTables() => new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
    {
        ["Crafting-D_ProcessorRecipes.json"] = new JsonObject
        {
            ["SmelterRecipe"] = new JsonObject { ["CraftTime"] = 3 },
        },
    };

    /// <summary>A field shaped exactly like a real recipe reference (RowName + an explicit DataTableName — see ExmodReferenceChecker's own doc comment) naming a table that can never exist in the fake (nonexistent) dataFolder CreateViewModel always uses — guaranteed to resolve as ReferenceResolution.TableNotFound, independent of any base-game data or self-declared rows.</summary>
    private static string BuildBrokenReferenceExmodJson(string fileName) => $$"""
        {
            "name": "{{fileName}}", "author": "Test", "version": "1", "description": "D",
            "fileName": "{{fileName}}",
            "Rows": [
                {"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                 "File_Items": [{"Name": "SmelterRecipe",
                     "Requirement": {"RowName": "SomeItem", "DataTableName": "D_ThisTableDoesNotExist"} }]}
            ]
        }
        """;

    // ---------------------------------------------------------------------------------------
    // 1. Queue mutation -> debounced conflict/validation recompute
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AddingToQueue_RestartsTheDebounceTimer_RatherThanRecomputingInline()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsRecomputeDebouncePending);

        // No real folder needed — the CollectionChanged handler below never touches disk itself,
        // it only resets state and restarts the debounce timer.
        vm.Queue.Add(MakeEntry("Placeholder"));

        Assert.True(vm.IsRecomputeDebouncePending);
    }

    [Fact]
    public async Task AddingConflictingMods_DoesNotSynchronouslyUpdateConflictCount_ButRecomputeThenReportsTheRealConflict()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        libraryRepository.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeExmodJson("ModB", craftTime: 99), out _);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);

        vm.Queue.Add(MakeEntry("ModA"));
        vm.Queue.Add(MakeEntry("ModB"));

        // Only the debounce timer restarted (DispatcherTimer never ticks without a live message
        // pump — see DebounceTimerTests' own doc comment) — ConflictCount is still whatever it was
        // before, not yet the real answer for this queue.
        Assert.Equal(0, vm.ConflictCount);
        Assert.True(vm.IsRecomputeDebouncePending);

        // Invoking the exact same recompute the timer would eventually have fired — proves it's
        // wired to the CURRENT queue and produces the right answer once it actually runs.
        await vm.RecomputeConflictCountAsync();

        Assert.Equal(1, vm.ConflictCount);
        Assert.True(vm.ConflictingModNamesByMod.ContainsKey("ModA"));
        Assert.True(vm.ConflictingModNamesByMod.ContainsKey("ModB"));
    }

    [Fact]
    public async Task RemovingTheConflictingMod_RecomputeReportsZeroConflictsAgain()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        libraryRepository.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeExmodJson("ModB", craftTime: 99), out _);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);
        vm.Queue.Add(MakeEntry("ModA"));
        vm.Queue.Add(MakeEntry("ModB"));
        await vm.RecomputeConflictCountAsync();
        Assert.Equal(1, vm.ConflictCount);

        vm.Queue.RemoveAt(1);
        await vm.RecomputeConflictCountAsync();

        Assert.Equal(0, vm.ConflictCount);
    }

    [Fact]
    public async Task RecomputeValidationIssueCountAsync_FlagsARealBrokenReference()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildBrokenReferenceExmodJson("ModA"), out _);
        var vm = CreateViewModel(libraryRepository: libraryRepository);

        vm.Queue.Add(MakeEntry("ModA"));
        Assert.Equal(0, vm.ValidationIssueCount);

        await vm.RecomputeValidationIssueCountAsync();

        Assert.Equal(1, vm.ValidationIssueCount);
    }

    // ---------------------------------------------------------------------------------------
    // 1b. Manual conflict picks must be invalidated whenever a queued mod's own content or the
    //     base data folder changes — not just on a Queue mutation. MergeEngine.Merge applies a
    //     pick as a plain index into that field's candidate list, rebuilt fresh at Rebuild time;
    //     if the content behind that index changed, the SAME index can now mean something else
    //     entirely, with no way to detect that from the index alone.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void LibraryChangedMessage_InvalidatesAnyStoredManualPick()
    {
        var vm = CreateViewModel();
        var pick = new Dictionary<(string, string, string), int> { [("File.json", "Item", "Field")] = 0 };
        vm.ManualPicksForTesting = pick;

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        Assert.Null(vm.ManualPicksForTesting);
    }

    [Fact]
    public void WeeklyChangeReportUpdatedMessage_InvalidatesAnyStoredManualPick()
    {
        var vm = CreateViewModel();
        var pick = new Dictionary<(string, string, string), int> { [("File.json", "Item", "Field")] = 0 };
        vm.ManualPicksForTesting = pick;

        WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

        Assert.Null(vm.ManualPicksForTesting);
    }

    // ---------------------------------------------------------------------------------------
    // 2. Package-fingerprint cache: invalidates on a real change, stays cached when nothing moved
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task PackageCache_ARealOnDiskEdit_IsPickedUpOnTheNextRecompute()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        libraryRepository.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeExmodJson("ModB", craftTime: 5), out var modBExmodPath);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);
        vm.Queue.Add(MakeEntry("ModA"));
        vm.Queue.Add(MakeEntry("ModB"));

        // Both currently agree (CraftTime 5) — populates the cache for both, no conflict yet.
        await vm.RecomputeConflictCountAsync();
        Assert.Equal(0, vm.ConflictCount);

        // A real edit — File.WriteAllText naturally bumps the file's own LastWriteTimeUtc, which is
        // exactly what FolderFingerprint keys on.
        File.WriteAllText(modBExmodPath, BuildCraftTimeExmodJson("ModB", craftTime: 99));

        await vm.RecomputeConflictCountAsync();

        Assert.Equal(1, vm.ConflictCount);
    }

    [Fact]
    public async Task PackageCache_AnEditThatLeavesTheFolderFingerprintUnchanged_StillServesTheStaleCachedContent()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        libraryRepository.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeExmodJson("ModB", craftTime: 5), out var modBExmodPath);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);
        vm.Queue.Add(MakeEntry("ModA"));
        vm.Queue.Add(MakeEntry("ModB"));
        await vm.RecomputeConflictCountAsync();
        Assert.Equal(0, vm.ConflictCount);

        // Same real content edit as the test above, EXCEPT the file's own LastWriteTimeUtc is
        // restored to exactly what it was before the edit — the folder's fingerprint (file count +
        // max LastWriteTimeUtc) is therefore identical to what GetOrReadPackage already cached, so
        // this proves the cache is a real cache (skips the disk read) rather than either "always
        // re-reads" (which would make this test see 99, i.e. a conflict) or "never invalidates"
        // (which the test above already rules out).
        var originalWriteUtc = File.GetLastWriteTimeUtc(modBExmodPath);
        File.WriteAllText(modBExmodPath, BuildCraftTimeExmodJson("ModB", craftTime: 99));
        File.SetLastWriteTimeUtc(modBExmodPath, originalWriteUtc);

        await vm.RecomputeConflictCountAsync();

        Assert.Equal(0, vm.ConflictCount);
    }

    // ---------------------------------------------------------------------------------------
    // 3. SuggestQueueOrderAsync's own reordering algorithm
    // ---------------------------------------------------------------------------------------

    /// <summary>4 fields, none present in base — all 4 are "real changes vs base" AND the raw field count is also 4.</summary>
    private static string BuildBigOverhaulJson() => """
        {
            "name": "BigOverhaul", "author": "Test", "version": "1", "description": "D", "fileName": "BigOverhaul",
            "Rows": [{"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                "File_Items": [{"Name": "Recipe1", "FieldA": "A1", "FieldB": "B1", "FieldE": "E1", "FieldF": "F1"}]}]
        }
        """;

    /// <summary>6 fields declared (a stale whole-item-copy's own inflated raw count), but FieldA-D are byte-identical to base — only FieldG/FieldH are real changes. Deliberately gives a HIGHER raw count (6) than BigOverhaul's 4, but a LOWER real-changes-vs-base count (2) — the discriminating case: sorting by raw count alone would rank this ahead of BigOverhaul; sorting by real changes (what this method exists to do — see its own doc comment) ranks it behind.</summary>
    private static string BuildStaleCopyJson() => """
        {
            "name": "StaleCopy", "author": "Test", "version": "1", "description": "D", "fileName": "StaleCopy",
            "Rows": [{"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                "File_Items": [{"Name": "Recipe1",
                    "FieldA": "base_a", "FieldB": "base_b", "FieldC": "base_c", "FieldD": "base_d",
                    "FieldG": "G1", "FieldH": "H1"}]}]
        }
        """;

    /// <summary>1 field, not in base — 1 real change, the fewest of the three, so it should end up in the highest-priority (last) slot among the EXMOD entries.</summary>
    private static string BuildSmallTweakJson() => """
        {
            "name": "SmallTweak", "author": "Test", "version": "1", "description": "D", "fileName": "SmallTweak",
            "Rows": [{"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                "File_Items": [{"Name": "Recipe1", "FieldZ": "Z1"}]}]
        }
        """;

    private static IReadOnlyDictionary<string, JsonObject> BuildBaseTables() => new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
    {
        ["Crafting-D_ProcessorRecipes.json"] = new JsonObject
        {
            ["Recipe1"] = new JsonObject
            {
                ["FieldA"] = "base_a",
                ["FieldB"] = "base_b",
                ["FieldC"] = "base_c",
                ["FieldD"] = "base_d",
            },
        },
    };

    [Fact]
    public async Task ComputeSuggestedQueueOrderAsync_OpaquePakStaysAtItsOwnIndex_ExmodEntriesFillTheRemainingSlotsSortedByRealChanges()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["BigOverhaul"] = CreateExmodFolder("BigOverhaul", BuildBigOverhaulJson(), out _);
        libraryRepository.FolderPaths["StaleCopy"] = CreateExmodFolder("StaleCopy", BuildStaleCopyJson(), out _);
        libraryRepository.FolderPaths["SmallTweak"] = CreateExmodFolder("SmallTweak", BuildSmallTweakJson(), out _);
        libraryRepository.FolderPaths["OpaquePak1"] = CreateOpaquePakFolder("OpaquePak1");
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);

        // Original order: [OpaquePak1, SmallTweak, BigOverhaul, StaleCopy].
        vm.Queue.Add(MakeEntry("OpaquePak1", isOpaquePak: true));
        vm.Queue.Add(MakeEntry("SmallTweak"));
        vm.Queue.Add(MakeEntry("BigOverhaul"));
        vm.Queue.Add(MakeEntry("StaleCopy"));

        var (newOrder, changed) = await vm.ComputeSuggestedQueueOrderAsync();

        Assert.True(changed);
        // OpaquePak1 stays at index 0 (its own original slot); the three EXMOD entries fill
        // indices 1-3 sorted descending by REAL changes vs base (BigOverhaul=4, StaleCopy=2,
        // SmallTweak=1) — NOT descending raw field count, which would instead rank StaleCopy (6
        // declared fields) ahead of BigOverhaul (4 declared fields).
        Assert.Equal(
            new[] { "OpaquePak1", "BigOverhaul", "StaleCopy", "SmallTweak" },
            newOrder.Select(e => e.FolderName));
    }

    [Fact]
    public async Task ComputeSuggestedQueueOrderAsync_QueueAlreadyInThatOrder_ReportsUnchanged()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["BigOverhaul"] = CreateExmodFolder("BigOverhaul", BuildBigOverhaulJson(), out _);
        libraryRepository.FolderPaths["StaleCopy"] = CreateExmodFolder("StaleCopy", BuildStaleCopyJson(), out _);
        libraryRepository.FolderPaths["SmallTweak"] = CreateExmodFolder("SmallTweak", BuildSmallTweakJson(), out _);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);

        // Already sorted descending by real changes vs base (4, 2, 1).
        vm.Queue.Add(MakeEntry("BigOverhaul"));
        vm.Queue.Add(MakeEntry("StaleCopy"));
        vm.Queue.Add(MakeEntry("SmallTweak"));

        var (_, changed) = await vm.ComputeSuggestedQueueOrderAsync();

        Assert.False(changed);
    }

    [Fact]
    public async Task SuggestQueueOrderCommand_FewerThanTwoRealMods_ReportsStatusAndNeverReachesTheConfirmationDialog()
    {
        // Only ever exercised through the public command here because this particular guard
        // returns BEFORE the method would ever construct a real ThemedMessageBox dialog — every
        // other branch of SuggestQueueOrderAsync is covered above via ComputeSuggestedQueueOrderAsync
        // instead, specifically to avoid that dialog (see this test class' own doc comment).
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["OpaquePak1"] = CreateOpaquePakFolder("OpaquePak1");
        var vm = CreateViewModel(libraryRepository: libraryRepository);
        vm.Queue.Add(MakeEntry("OpaquePak1", isOpaquePak: true));

        await vm.SuggestQueueOrderCommand.ExecuteAsync(null);

        Assert.Contains("Nothing to reorder", vm.StatusMessage);
    }

    // ---------------------------------------------------------------------------------------
    // 4. RebuildAndInstallAsync's own top-level orchestration
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RebuildAndInstall_EmptyQueueNoGameplayOptions_NeitherRebuildNorInstallEverRuns()
    {
        var rebuildService = new FakeRebuildService();
        var installService = new FakeInstallService();
        var vm = CreateViewModel(rebuildService: rebuildService, installService: installService);

        await vm.RebuildAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(0, rebuildService.RebuildCallCount);
        Assert.Equal(0, installService.InstallCallCount);
        Assert.Contains("Add at least one mod", vm.StatusMessage);
    }

    [Fact]
    public async Task RebuildAndInstall_NoUnrealPakExePathConfigured_GuardsOutBeforeEitherFakeIsCalled()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var rebuildService = new FakeRebuildService();
        var installService = new FakeInstallService();
        var settingsService = new FakeSettingsService(); // UnrealPakExePath left null
        var vm = CreateViewModel(
            libraryRepository: libraryRepository, rebuildService: rebuildService, installService: installService, settingsService: settingsService);
        vm.Queue.Add(MakeEntry("ModA"));

        await vm.RebuildAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(0, rebuildService.RebuildCallCount);
        Assert.Equal(0, installService.InstallCallCount);
        Assert.Contains("UnrealPak.exe", vm.StatusMessage);
    }

    [Fact]
    public async Task RebuildAndInstall_RebuildSucceeds_ReallyChainsIntoInstall_WhichGuardsOutOnMissingContentPath()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var rebuildService = new FakeRebuildService
        {
            Result = new RebuildResult(MergedFileCount: 1, PackedFileCount: 5, OutputPakPath: "out.pak", ManifestPath: "out.manifest", Warnings: [], Notes: []),
        };
        var installService = new FakeInstallService();
        var settingsService = new FakeSettingsService();
        settingsService.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        // IcarusContentPath deliberately left unset — InstallAsync's own FIRST guard, which fires
        // before it ever constructs a ThemedMessageBox confirmation dialog.
        var vm = CreateViewModel(
            libraryRepository: libraryRepository, rebuildService: rebuildService, installService: installService, settingsService: settingsService);
        vm.Queue.Add(MakeEntry("ModA"));

        await vm.RebuildAndInstallCommand.ExecuteAsync(null);

        // Rebuild really ran (not skipped) ...
        Assert.Equal(1, rebuildService.RebuildCallCount);
        Assert.True(vm.CanCopyToGame);
        Assert.Equal(1, libraryRepository.ImportPakCallCount);
        // ... and RebuildAndInstallAsync really did chain into InstallAsync afterward (not just stop
        // after a successful Rebuild) — proven by InstallAsync's OWN guard message showing up, even
        // though the fake install service itself was never actually invoked.
        Assert.Equal(0, installService.InstallCallCount);
        Assert.Contains("Icarus Content folder", vm.InstallStatusMessage);
    }

    [Fact]
    public async Task RebuildAndInstall_RebuildThrows_InstallIsNeverAttempted()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var rebuildService = new FakeRebuildService { ExceptionToThrow = new InvalidOperationException("UnrealPak exploded") };
        var installService = new FakeInstallService();
        var settingsService = new FakeSettingsService();
        settingsService.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        settingsService.Current.IcarusContentPath = @"C:\fake\Icarus"; // even with a real-looking path configured...
        var vm = CreateViewModel(
            libraryRepository: libraryRepository, rebuildService: rebuildService, installService: installService, settingsService: settingsService);
        vm.Queue.Add(MakeEntry("ModA"));

        await vm.RebuildAndInstallCommand.ExecuteAsync(null);

        // ...Install must never run off a Rebuild that didn't actually happen (see
        // RebuildAndInstallAsync's own doc comment on exactly this guarantee).
        Assert.Equal(0, installService.InstallCallCount);
        Assert.Contains("Rebuild failed", vm.StatusMessage);
        Assert.False(vm.IsRebuilding);
    }

    [Fact]
    public async Task RebuildAsync_Success_SurfacesWarningsAndNotes_AndImportsTheBuiltPakIntoLibrary()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var rebuildService = new FakeRebuildService
        {
            Result = new RebuildResult(
                MergedFileCount: 3, PackedFileCount: 10, OutputPakPath: "out.pak", ManifestPath: "out.manifest",
                Warnings: ["a real warning"], Notes: ["a real note"]),
        };
        var settingsService = new FakeSettingsService();
        settingsService.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService, settingsService: settingsService);
        vm.Queue.Add(MakeEntry("ModA"));

        var succeeded = await vm.RebuildAsync();

        Assert.True(succeeded);
        Assert.True(vm.CanCopyToGame);
        Assert.Contains(vm.Warnings, w => w == "a real warning");
        Assert.Contains(vm.Warnings, w => w == "Note: a real note");
        Assert.Equal(1, libraryRepository.ImportPakCallCount);
        Assert.Single(rebuildService.LastQueuedMods!);
    }

    // ---------------------------------------------------------------------------------------
    // Fakes — one per constructor dependency, following this project's established
    // fake-per-interface convention (SavesViewModelTests/LibraryItemViewModelTests) rather than a
    // mocking framework. Every method not exercised by a test above throws NotSupportedException,
    // same convention those two files already use, so an accidental real call fails loudly instead
    // of silently returning a default.
    // ---------------------------------------------------------------------------------------

    private sealed class FakeLibraryRepository : ILibraryRepository
    {
        public Dictionary<string, string> FolderPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<LibraryEntry> AllEntries { get; set; } = [];
        public int ImportPakCallCount { get; private set; }
        public string? LastImportPakPath { get; private set; }

        public IReadOnlyList<LibraryEntry> GetAll() => AllEntries;

        public IReadOnlyList<string> UnreadableFolders => [];

        public IReadOnlyList<LibraryEntry> Search(string query) => throw new NotSupportedException("Not exercised by these tests.");

        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null)
        {
            ImportPakCallCount++;
            LastImportPakPath = pakFilePath;
            return new LibraryEntry
            {
                FolderName = Path.GetFileNameWithoutExtension(pakFilePath), Name = "Built pak", Author = "IcarusStarlink", Version = "1",
                Description = "", FileName = "Built", IsOpaquePak = true,
            };
        }

        public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void Refresh() => throw new NotSupportedException("Not exercised by these tests.");

        public void Delete(string folderName) { }

        public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void MarkLocallyEdited(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public void MarkConvertedFromPrebuiltPak(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public void SetDisplayNameOverride(string folderName, string? displayName) => throw new NotSupportedException("Not exercised by these tests.");

        public void LinkToNexus(string folderName, int nexusModId) => throw new NotSupportedException("Not exercised by these tests.");

        public void SetCatalogEntry(string folderName, string catalogEntryId) => throw new NotSupportedException("Not exercised by these tests.");

        public string BackupMod(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public bool HasModBackup(string folderName) => false;

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

        public string GetFolderPath(string folderName) => FolderPaths[folderName];
    }

    private sealed class FakeRebuildService : IRebuildService
    {
        public int RebuildCallCount { get; private set; }
        public IReadOnlyList<ExmodPackageContents>? LastQueuedMods { get; private set; }
        public RebuildResult Result { get; set; } = new(0, 0, "out.pak", "out.manifest", [], []);
        public Exception? ExceptionToThrow { get; set; }
        public Func<IEnumerable<string>, string, MergeReport, IReadOnlyDictionary<string, JsonObject>>? ReadKeyedBaseTablesHandler { get; set; }

        public Task<RebuildResult> RebuildAsync(
            IReadOnlyList<ExmodPackageContents> queuedMods, GameplayOptions gameplayOptions, string dataFolder, string unrealPakExePath,
            string outputPakPath, IReadOnlyList<string> prebuiltPakFilePaths,
            IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? manualPicks = null,
            IProgress<RebuildStageProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            RebuildCallCount++;
            LastQueuedMods = queuedMods;
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<(string PakName, IReadOnlyList<FieldChange> Changes)>> ComputePrebuiltPakFieldChangesAsync(
            IReadOnlyList<string> prebuiltPakFilePaths, string dataFolder, string unrealPakExePath, MergeReport report,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<(string PakName, IReadOnlyList<FieldChange> Changes)>>([]);

        public IReadOnlyDictionary<string, JsonObject> ReadKeyedBaseTables(IEnumerable<string> currentFiles, string dataFolder, MergeReport report) =>
            ReadKeyedBaseTablesHandler?.Invoke(currentFiles, dataFolder, report)
                ?? throw new NotSupportedException("Not exercised by these tests — configure ReadKeyedBaseTablesHandler if a test needs real base-table data.");
    }

    private sealed class FakeInstallService : IInstallService
    {
        public int InstallCallCount { get; private set; }
        public InstallResult Result { get; set; } = new("installed.pak", null);

        public Task<InstallResult> InstallAsync(string stagedPakPath, string icarusContentPath, string backupDirectory, CancellationToken cancellationToken = default)
        {
            InstallCallCount++;
            return Task.FromResult(Result);
        }

        public Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstalledState([]));

        public Task<bool> RemoveInstalledPakAsync(string stagedPakFileName, string icarusContentPath, string backupDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeProfileStore : IProfileStore
    {
        private readonly Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> ProfileNames => [.. _profiles.Keys];

        public Profile Load(string name) => _profiles[name];

        public void Save(Profile profile) => _profiles[profile.Name] = profile;

        public void Delete(string name) => _profiles.Remove(name);

        public void Rename(string oldName, string newName) => throw new NotSupportedException("Not exercised by these tests.");

        public bool HasBackup(string name) => false;

        public bool RestoreLatestBackup(string name) => false;
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public bool Save() => true;
    }

    private sealed class FakePatchService : IPatchService
    {
        public Task ExportAsync(PatchManifest manifest, IReadOnlyDictionary<string, ExmodPackageContents> bundledMods, string outputPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<PatchImportContents> ImportAsync(string patchFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeDaedalusCatalogClient : IDaedalusCatalogClient
    {
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeJimk72CatalogClient : IJimk72CatalogClient
    {
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakePakCompareService : IPakCompareService
    {
        public Task<PakCompareResult> CompareAsync(string unrealPakExePath, string firstPakPath, string secondPakPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeActivityLog : IActivityLog
    {
        public System.Collections.ObjectModel.ObservableCollection<ActivityEntry> Entries { get; } = [];

        public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info) =>
            Entries.Insert(0, new ActivityEntry(message, kind, DateTimeOffset.Now));
    }
}
