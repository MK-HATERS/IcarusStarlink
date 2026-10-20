using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Services;
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
/// MergeInstallViewModel's rebuild/install orchestration, against a fake per constructor
/// dependency. Every Yes/No question goes through its Confirm seam, names through
/// PromptForProfileName and the patch file through PickPatchFile, so Install, Remove from game,
/// Restore previous install, Suggest order and the profile commands all run here; only Review
/// conflicts and Validate mods still open a real window (MergeConflictsWindow,
/// ValidationIssueReportWindow), so those are reached through the same recompute the badges use
/// (RecomputeConflictCountAsync/RecomputeValidationIssueCountAsync, CreateConflictReviewForTesting).
/// The first groups below cover:
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

    // One messenger per test class instance — the shared WeakReferenceMessenger.Default made these
    // tests interfere with each other (and with every other test class) under the parallel runner.
    private readonly IMessenger _messenger = new WeakReferenceMessenger();

    private MergeInstallViewModel CreateViewModel(
        FakeLibraryRepository? libraryRepository = null,
        FakeRebuildService? rebuildService = null,
        FakeInstallService? installService = null,
        FakeProfileStore? profileStore = null,
        FakeSettingsService? settingsService = null,
        bool gameRunning = false,
        FakeGameDataFreshness? gameDataFreshness = null,
        FakePatchService? patchService = null)
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
            patchService ?? new FakePatchService(),
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
            Path.Combine(_tempRoot, "Backups"),
            _messenger,
            new FakeGameProcessChecker(gameRunning),
            gameDataFreshness);
    }

    private sealed class FakeGameProcessChecker(bool running) : IGameProcessChecker
    {
        public bool IsRunning() => running;
    }

    private sealed class FakeGameDataFreshness : IGameDataFreshness
    {
        public bool Outdated { get; set; }
        public bool RefreshSucceeds { get; set; } = true;
        public int RefreshCallCount { get; private set; }

        public Task<bool> IsGameDataOutdatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Outdated);

        public Task<bool> RefreshGameDataAsync()
        {
            RefreshCallCount++;
            if (RefreshSucceeds)
            {
                Outdated = false;
            }

            return Task.FromResult(RefreshSucceeds);
        }
    }

    /// <summary>ArcFission Library as the game has it installed: its folder in ue4ss\Mods, turned on.</summary>
    private static void InstallArcFissionLibrary(string contentPath)
    {
        var folder = Path.Combine(Path.GetDirectoryName(contentPath)!, "Binaries", "Win64", "ue4ss", "Mods", "ArcFissionLibrary");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "enabled.txt"), "");
    }

    private (FakeLibraryRepository Library, FakeSettingsService Settings) TwoConflictingMods(string nameA = "ModA", string nameB = "ModB")
    {
        var settings = new FakeSettingsService();
        settings.Current.UnrealPakExePath = Path.Combine(_tempRoot, "UnrealPak.exe");
        var a = MakeEntry("ModA");
        a.Name = nameA;
        var b = MakeEntry("ModB");
        b.Name = nameB;
        var library = new FakeLibraryRepository { AllEntries = [a, b] };
        library.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        library.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeExmodJson("ModB", craftTime: 99), out _);
        return (library, settings);
    }

    private static readonly (string, string, string) CraftTimeKey = ("Crafting-D_ProcessorRecipes.json", "SmelterRecipe", "CraftTime");

    private static string CraftTimeWinnerKey => MergeConflictsViewModel.WinnerKey(CraftTimeKey.Item1, CraftTimeKey.Item2, CraftTimeKey.Item3);

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

    /// <summary>The ticked mods and options save themselves (into "Default" when no profile is picked), so a restart keeps them without Save profile.</summary>
    [Fact]
    public void TheQueueSavesItself_AndComesBackOnTheNextStart()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB")] };
        var store = new FakeProfileStore();
        var settings = new FakeSettingsService();
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, settingsService: settings);

        vm.Queue.Add(library.AllEntries[0]);
        vm.Queue.Add(library.AllEntries[1]);
        vm.RemoveLevelCap = true;
        vm.SaveWorkingState();

        Assert.Equal("Default", vm.SelectedProfileName);
        Assert.Equal(["ModA", "ModB"], store.Load("Default").MergeQueueFolderNames);
        Assert.True(store.Load("Default").Options.RemoveLevelCap);

        var next = CreateViewModel(libraryRepository: library, profileStore: store, settingsService: settings);
        Assert.Equal(["ModA", "ModB"], next.Queue.Select(e => e.FolderName));
    }

    /// <summary>The profile last in use is the one selected on the next start.</summary>
    [Fact]
    public void TheLastProfileUsed_IsSelectedOnTheNextStart()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA")] };
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Default" });
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["ModA"] });
        var settings = new FakeSettingsService();
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, settingsService: settings);

        vm.SelectedProfileName = "Coop";

        var next = CreateViewModel(libraryRepository: library, profileStore: store, settingsService: settings);
        Assert.Equal("Coop", next.SelectedProfileName);
        Assert.Equal(["ModA"], next.Queue.Select(e => e.FolderName));
    }

    /// <summary>When the ticks were lost but the game still has its merged pak, its mods are ticked again in merge order.</summary>
    [Fact]
    public void RestoreQueueFromGame_TicksWhatTheInstalledPakWasBuiltFrom_InItsOrder()
    {
        var pak = MakeEntry("WirelessPower", isOpaquePak: true);
        pak.PakFileNames = ["ZZ_WirelessPower_Icon_P.pak"];
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB"), pak] };
        var vm = CreateViewModel(libraryRepository: library);
        vm.LastInstalledModNames = ["ModB", "Gone_Mod", "ModA", "ZZ_WirelessPower_Icon_P"];

        Assert.Equal(3, vm.RestoreQueueFromGame());
        Assert.Equal(["ModB", "ModA", "WirelessPower"], vm.Queue.Select(e => e.FolderName));
    }

    /// <summary>With the name box gone, New profile… and Rename profile… ask for the name; cancelling changes nothing.</summary>
    [Fact]
    public void NewAndRenameProfile_AskForTheName()
    {
        var store = new FakeProfileStore();
        var vm = CreateViewModel(profileStore: store);
        var answer = (string?)"Coop run";
        vm.PromptForProfileName = (_, _) => answer;

        vm.NewProfileCommand.Execute(null);
        Assert.Contains("Coop run", store.ProfileNames);
        Assert.Equal("Coop run", vm.SelectedProfileName);

        answer = null;
        vm.RenameProfileCommand.Execute(null);
        Assert.Contains("Coop run", store.ProfileNames);

        answer = "Solo run";
        vm.RenameProfileCommand.Execute(null);
        Assert.Contains("Solo run", store.ProfileNames);
        Assert.DoesNotContain("Coop run", store.ProfileNames);
    }

    /// <summary>Renaming the profile in use makes it the one selected on the next start.</summary>
    [Fact]
    public void RenameProfile_RemembersTheNewNameForTheNextStart()
    {
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop" });
        var settings = new FakeSettingsService();
        var vm = CreateViewModel(profileStore: store, settingsService: settings);
        vm.SelectedProfileName = "Coop";
        vm.PromptForProfileName = (_, _) => "Solo";

        vm.RenameProfileCommand.Execute(null);

        Assert.Equal("Solo", vm.SelectedProfileName);
        Assert.Equal("Solo", settings.Current.LastMergeProfileName);
    }

    /// <summary>Deleting a profile with an unsaved change loads "Default" as saved; the change is written nowhere.</summary>
    [Fact]
    public void DeleteProfile_WithAPendingChange_LoadsDefaultAndNeverWritesTheDeletedQueueAnywhere()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB")] };
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Default", MergeQueueFolderNames = ["ModA"] });
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["ModB"] });
        var vm = CreateViewModel(libraryRepository: library, profileStore: store);
        vm.SelectedProfileName = "Coop";
        vm.Queue.Add(library.AllEntries[0]); // pending auto-save of Coop

        vm.DeleteProfileCommand.Execute(null);
        vm.SaveWorkingState();

        Assert.DoesNotContain("Coop", store.ProfileNames);
        Assert.Equal("Default", vm.SelectedProfileName);
        Assert.Equal(["ModA"], vm.Queue.Select(e => e.FolderName));
        Assert.Equal(["ModA"], store.Load("Default").MergeQueueFolderNames);
    }

    /// <summary>Deleting the only profile empties the queue and options and saves nothing.</summary>
    [Fact]
    public void DeleteProfile_NoDefaultToFallBackTo_StartsEmptyWithoutSaving()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA")] };
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["ModA"], Options = new GameplayOptions { RemoveLevelCap = true } });
        var vm = CreateViewModel(libraryRepository: library, profileStore: store);
        vm.SelectedProfileName = "Coop";
        vm.UnlimitedAmmo = true; // pending auto-save of Coop

        vm.DeleteProfileCommand.Execute(null);
        vm.SaveWorkingState();

        Assert.Null(vm.SelectedProfileName);
        Assert.Empty(vm.Queue);
        Assert.False(vm.RemoveLevelCap);
        Assert.Empty(store.ProfileNames);
    }

    /// <summary>ArcFission's prompt shows only with a merge installed that its newest data pak isn't part of.</summary>
    [Fact]
    public async Task ArcFissionNeedsMerge_OnlyWhenAMergeIsInstalledWithoutItsNewestPak()
    {
        var contentPath = Path.Combine(_tempRoot, "Content");
        var modsFolder = Path.Combine(contentPath, "Paks", "mods");
        Directory.CreateDirectory(modsFolder);
        File.WriteAllBytes(Path.Combine(modsFolder, "ZZ_ArcFission_Data_3_P.pak"), []);
        InstallArcFissionLibrary(contentPath);
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService();
        var vm = CreateViewModel(installService: install, settingsService: settings);

        await vm.RefreshInstalledModNamesAsync();
        Assert.False(vm.ArcFissionNeedsMerge); // nothing installed

        install.InstalledModNames = ["ModA", "ZZ_ArcFission_Data_2_P"];
        await vm.RefreshInstalledModNamesAsync();
        Assert.True(vm.ArcFissionNeedsMerge);

        install.InstalledModNames = ["ModA", "ZZ_ArcFission_Data_3_P"];
        await vm.RefreshInstalledModNamesAsync();
        Assert.False(vm.ArcFissionNeedsMerge);
    }

    /// <summary>
    /// ArcFission's data pak goes to Rebuild as the lowest-priority input, and a saved winner's
    /// index counts it first, the same order Rebuild merges in.
    /// </summary>
    [Fact]
    public async Task Rebuild_ArcFissionPakIsLowestPriority_AndSavedWinnerIndexCountsIt()
    {
        var contentPath = Path.Combine(_tempRoot, "Content");
        var modsFolder = Path.Combine(contentPath, "Paks", "mods");
        Directory.CreateDirectory(modsFolder);
        var arcFissionPak = Path.Combine(modsFolder, "ZZ_ArcFission_Data_3_P.pak");
        File.WriteAllBytes(arcFissionPak, []);
        InstallArcFissionLibrary(contentPath);
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = contentPath;
        settings.Current.UnrealPakExePath = Path.Combine(_tempRoot, "UnrealPak.exe");

        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB")] };
        library.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        library.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeExmodJson("ModB", craftTime: 99), out _);
        var rebuild = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        rebuild.PrebuiltPakChanges[arcFissionPak] =
            [new FieldChange("Crafting-D_ProcessorRecipes.json", "SmelterRecipe", "CraftTime", JsonValue.Create(3), JsonValue.Create(42), ValueSemantic.Scalar)];
        var store = new FakeProfileStore();
        store.Save(new Profile
        {
            Name = "Default",
            MergeQueueFolderNames = ["ModA", "ModB"],
            ConflictWinners = new() { [MergeConflictsViewModel.WinnerKey("Crafting-D_ProcessorRecipes.json", "SmelterRecipe", "CraftTime")] = "ModA" },
        });
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: store, settingsService: settings);

        Assert.True(await vm.RebuildAsync());

        Assert.Equal([arcFissionPak], rebuild.LastLowPriorityPrebuiltPakFilePaths);
        Assert.Empty(rebuild.LastPrebuiltPakFilePaths!);
        // Candidates in merge order: ArcFission (0), ModA (1), ModB (2).
        Assert.Equal(1, rebuild.LastManualPicks![("Crafting-D_ProcessorRecipes.json", "SmelterRecipe", "CraftTime")]);
    }

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

    /// <summary>Two mods that disagree on one value AND both add a new item under the same name (that part merges itself, nothing to pick).</summary>
    private static string BuildCraftTimeAndNewItemExmodJson(string fileName, int craftTime, string newItemField) => $$"""
        {
            "name": "{{fileName}}", "author": "Test", "version": "1", "description": "D",
            "fileName": "{{fileName}}",
            "Rows": [
                {"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                 "File_Items": [{"Name": "SmelterRecipe", "CraftTime": {{craftTime}}},
                                {"Name": "BrandNewRecipe", "{{newItemField}}": 1}]}
            ]
        }
        """;

    /// <summary>
    /// The real 22-vs-1 bug: the bar counted conflicts plus same-name new items (1 + 21 = 22) while
    /// Review conflicts listed only the 1 conflict. Now the bar's count is the window's own list,
    /// and the same-name new items are mentioned on their own as combined automatically.
    /// </summary>
    [Fact]
    public async Task ConflictBar_CountsExactlyWhatReviewConflictsLists_AndNamesWhatWasCombined()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeAndNewItemExmodJson("ModA", craftTime: 5, newItemField: "Speed"), out _);
        libraryRepository.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeAndNewItemExmodJson("ModB", craftTime: 99, newItemField: "Power"), out _);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);
        vm.Queue.Add(MakeEntry("ModA"));
        vm.Queue.Add(MakeEntry("ModB"));

        await vm.RecomputeConflictCountAsync();

        var review = vm.CreateConflictReviewForTesting();
        Assert.Equal(1, vm.ConflictCount);
        Assert.Equal(review.AllConflicts.Count, vm.ConflictCount);
        Assert.Equal(review.ConflictsView.Cast<object>().Count(), vm.ConflictCount);
        Assert.Equal(review.Tally, vm.ConflictTally);
        Assert.Equal(1, vm.ConflictTally.CombinedAutomatically);
        Assert.Equal("1 value is changed differently by two mods — pick a winner, or the mod lower in the queue wins.", vm.ConflictTally.BannerText);
        Assert.Contains("combined automatically", vm.ConflictTally.CombinedText);
        Assert.StartsWith("1 value is changed differently", review.Summary);
    }

    /// <summary>Only a same-name new item: nothing to decide, so no bar and no ⚠ on the rows.</summary>
    [Fact]
    public async Task ConflictBar_OnlySameNameNewItems_ShowsNoConflict()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeAndNewItemExmodJson("ModA", craftTime: 5, newItemField: "Speed"), out _);
        libraryRepository.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeAndNewItemExmodJson("ModB", craftTime: 5, newItemField: "Power"), out _);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);
        vm.Queue.Add(MakeEntry("ModA"));
        vm.Queue.Add(MakeEntry("ModB"));

        await vm.RecomputeConflictCountAsync();

        Assert.False(vm.HasConflicts);
        Assert.Equal(1, vm.ConflictTally.CombinedAutomatically);
        Assert.Empty(vm.ConflictingModNamesByMod);
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

    /// <summary>Two queued mods each adding a workshop node to the same spot of one tree: fine alone, one hides the other together.</summary>
    [Fact]
    public async Task RecomputeValidationIssueCountAsync_FlagsTwoModsNodesOnTopOfEachOther()
    {
        const string treeRow = """{"CurrentFile": "Talents-D_TalentTrees.json", "File_Items": [{"Name": "Workshop_Extras"}]},""";
        static string Node(string mod, string node, bool addsTree) => $$$"""
            {
                "name": "{{{mod}}}", "author": "Test", "version": "1", "description": "D", "fileName": "{{{mod}}}",
                "Rows": [
                    {{{(addsTree ? treeRow : "")}}}
                    {"CurrentFile": "Talents-D_Talents.json",
                     "File_Items": [{"Name": "{{{node}}}", "TalentTree": {"RowName": "Workshop_Extras"},
                                     "Position": {"X": 500, "Y": 450}, "Size": {"X": 250, "Y": 250}}]}
                ]
            }
            """;
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", Node("ModA", "Workshop_Gold_Axe", addsTree: true), out _);
        libraryRepository.FolderPaths["ModB"] = CreateExmodFolder("ModB", Node("ModB", "Workshop_Bone_Axe", addsTree: false), out _);
        var vm = CreateViewModel(libraryRepository: libraryRepository);
        vm.Queue.Add(MakeEntry("ModA"));
        await vm.RecomputeValidationIssueCountAsync();
        Assert.Equal(0, vm.ValidationIssueCount);

        vm.Queue.Add(MakeEntry("ModB"));
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

        _messenger.Send(new LibraryChangedMessage());

        Assert.Null(vm.ManualPicksForTesting);
    }

    [Fact]
    public void WeeklyChangeReportUpdatedMessage_InvalidatesAnyStoredManualPick()
    {
        var vm = CreateViewModel();
        var pick = new Dictionary<(string, string, string), int> { [("File.json", "Item", "Field")] = 0 };
        vm.ManualPicksForTesting = pick;

        _messenger.Send(new WeeklyChangeReportUpdatedMessage());

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
        // The guard returns before anything is asked: a prebuilt pak keeps its place, so one alone
        // has nothing to reorder around it.
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["OpaquePak1"] = CreateOpaquePakFolder("OpaquePak1");
        var vm = CreateViewModel(libraryRepository: libraryRepository);
        vm.Confirm = (_, _) => throw new InvalidOperationException("Must not ask.");
        vm.Queue.Add(MakeEntry("OpaquePak1", isOpaquePak: true));

        await vm.SuggestQueueOrderCommand.ExecuteAsync(null);

        Assert.Contains("Nothing to reorder", vm.StatusMessage);
    }

    // ---------------------------------------------------------------------------------------
    // 4. RebuildAndInstallAsync's own top-level orchestration
    // ---------------------------------------------------------------------------------------

    /// <summary>The one install path that had no game-running guard: Icarus holds its Paks folder open, so a pak copied under it can leave a half-replaced pack.</summary>
    [Fact]
    public async Task RebuildAndInstall_IcarusIsRunning_RebuildsButRefusesToInstall()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var rebuildService = new FakeRebuildService
        {
            Result = new RebuildResult(MergedFileCount: 1, PackedFileCount: 1, OutputPakPath: "out.pak", ManifestPath: "out.manifest", Warnings: [], Notes: []),
        };
        var installService = new FakeInstallService();
        var settingsService = new FakeSettingsService();
        settingsService.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        settingsService.Current.IcarusContentPath = Path.Combine(_tempRoot, "FakeContent");
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService, installService: installService, settingsService: settingsService, gameRunning: true);
        vm.Queue.Add(MakeEntry("ModA"));

        await vm.RebuildAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(1, rebuildService.RebuildCallCount);
        Assert.Equal(0, installService.InstallCallCount);
        Assert.Contains("Icarus is running", vm.InstallStatusMessage);
    }

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

    /// <summary>One download that shipped two paks is ONE opaque Library entry (ILibraryRepository.ImportPaks) — the rebuild must fold in every pak in its folder, not just the first.</summary>
    [Fact]
    public async Task RebuildAsync_OpaqueEntryHoldingTwoPaks_FoldsInBothPaks()
    {
        var libraryRepository = new FakeLibraryRepository();
        var bundleFolder = CreateOpaquePakFolder("WirelessPower");
        File.WriteAllBytes(Path.Combine(bundleFolder, "ZZ_WirelessPower_Model_P.pak"), [5, 6, 7, 8]);
        libraryRepository.FolderPaths["WirelessPower"] = bundleFolder;
        var rebuildService = new FakeRebuildService
        {
            Result = new RebuildResult(
                MergedFileCount: 0, PackedFileCount: 2, OutputPakPath: "out.pak", ManifestPath: "out.manifest", Warnings: [], Notes: []),
        };
        var settingsService = new FakeSettingsService();
        settingsService.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService, settingsService: settingsService);
        vm.Queue.Add(MakeEntry("WirelessPower", isOpaquePak: true));

        var succeeded = await vm.RebuildAsync();

        Assert.True(succeeded);
        Assert.NotNull(rebuildService.LastPrebuiltPakFilePaths);
        Assert.Equal(["Prebuilt.pak", "ZZ_WirelessPower_Model_P.pak"], rebuildService.LastPrebuiltPakFilePaths!.Select(Path.GetFileName).ToList());
    }

    /// <summary>
    /// Audit finding: RebuildAsync's own Library import sends LibraryChangedMessage, and this VM's
    /// handler for that message wiped the manual conflict picks — so a second Install of the same
    /// reviewed queue silently resolved last-wins. Picks must survive the self-sent message.
    /// </summary>
    [Fact]
    public async Task RebuildAsync_Success_KeepsTheManualConflictPicksForTheNextRebuild()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var rebuildService = new FakeRebuildService
        {
            Result = new RebuildResult(
                MergedFileCount: 1, PackedFileCount: 1, OutputPakPath: "out.pak", ManifestPath: "out.manifest", Warnings: [], Notes: []),
        };
        var settingsService = new FakeSettingsService();
        settingsService.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService, settingsService: settingsService);
        vm.Queue.Add(MakeEntry("ModA"));
        var pick = new Dictionary<(string, string, string), int> { [("Crafting-D_ProcessorRecipes.json", "SmelterRecipe", "CraftTime")] = 0 };
        vm.ManualPicksForTesting = pick;

        var succeeded = await vm.RebuildAsync();

        Assert.True(succeeded);
        Assert.Equal(1, libraryRepository.ImportPakCallCount);
        Assert.Same(pick, vm.ManualPicksForTesting);
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
    // Review fixes: game-update guard, build snapshot, queue order, ISL packs, missing mods,
    // winners by folder, ArcFission leftovers, install texts, Restore previous install
    // ---------------------------------------------------------------------------------------

    /// <summary>After a game update, a build without Refresh game data first would put old tables back: declining the refresh builds nothing.</summary>
    [Fact]
    public async Task Rebuild_GameUpdatedSinceTheLastRefresh_DecliningTheRefreshBuildsNothing()
    {
        var (library, settings) = TwoConflictingMods();
        var rebuild = new FakeRebuildService();
        var freshness = new FakeGameDataFreshness { Outdated = true };
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings, gameDataFreshness: freshness);
        vm.Queue.Add(library.AllEntries[0]);
        string? asked = null;
        vm.Confirm = (message, _) => { asked = message; return false; };

        Assert.False(await vm.RebuildAsync());

        Assert.Contains("Refresh game data", asked);
        Assert.Equal(0, rebuild.RebuildCallCount);
        Assert.Equal(0, freshness.RefreshCallCount);
        Assert.Contains("Refresh game data", vm.StatusMessage);
        Assert.False(vm.IsRebuilding);
    }

    /// <summary>Yes refreshes the game data first and then builds; a refresh that fails builds nothing.</summary>
    [Fact]
    public async Task Rebuild_GameUpdatedSinceTheLastRefresh_YesRefreshesThenBuilds()
    {
        var (library, settings) = TwoConflictingMods();
        var rebuild = new FakeRebuildService();
        var freshness = new FakeGameDataFreshness { Outdated = true, RefreshSucceeds = false };
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings, gameDataFreshness: freshness);
        vm.Queue.Add(library.AllEntries[0]);
        vm.Confirm = (_, _) => true;

        Assert.False(await vm.RebuildAsync());
        Assert.Equal(1, freshness.RefreshCallCount);
        Assert.Equal(0, rebuild.RebuildCallCount);

        freshness.RefreshSucceeds = true;
        Assert.True(await vm.RebuildAsync());
        Assert.Equal(2, freshness.RefreshCallCount);
        Assert.Equal(1, rebuild.RebuildCallCount);

        // Up to date now: no question, no refresh.
        vm.Confirm = (_, _) => throw new InvalidOperationException("Nothing to ask.");
        Assert.True(await vm.RebuildAsync());
        Assert.Equal(2, freshness.RefreshCallCount);
    }

    /// <summary>
    /// What's built is what was queued when the build started: queue edits arriving mid-build (the
    /// Library's ON box) are refused with a reason, and a profile switch mid-build doesn't change
    /// which profile the built pack is filed under.
    /// </summary>
    [Fact]
    public async Task Rebuild_EditsWhileBuilding_AreRefused_AndTheBuildUsesItsSnapshot()
    {
        var (library, settings) = TwoConflictingMods();
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["ModA"] });
        store.Save(new Profile { Name = "Solo", MergeQueueFolderNames = ["ModB"] });
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: store, settingsService: settings);
        vm.SelectedProfileName = "Coop";
        string? refusal = null;
        rebuild.DuringRebuild = () =>
        {
            Assert.True(vm.IsRebuilding);
            Assert.False(vm.IsNotRebuilding);
            vm.AddToQueueByFolderNames(["ModB"]);
            refusal = vm.StatusMessage;
            vm.RemoveFromQueueByFolderNames(["ModA"]);
            vm.SelectedProfileName = "Solo";
            return Task.CompletedTask;
        };

        Assert.True(await vm.RebuildAsync());

        Assert.Contains("building", refusal);
        Assert.Equal("ModA", Assert.Single(rebuild.LastQueuedMods!).Package.Name);
        Assert.Equal("Coop", library.LastMergedPackProfileName);
        Assert.True(vm.IsNotRebuilding);
    }

    /// <summary>A prebuilt pak's queue position reaches Rebuild (how many EXMOD mods come before it), so it merges in true queue order.</summary>
    [Fact]
    public async Task Rebuild_PassesEachPrebuiltPaksQueuePosition()
    {
        var (library, settings) = TwoConflictingMods();
        library.FolderPaths["SomePak"] = CreateOpaquePakFolder("SomePak");
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        vm.Queue.Add(MakeEntry("SomePak", isOpaquePak: true));
        vm.Queue.Add(library.AllEntries[1]);

        Assert.True(await vm.RebuildAsync());

        Assert.Equal([1], rebuild.LastPrebuiltPakQueuePositions);
        Assert.Equal(["ModA", "ModB"], rebuild.LastQueuedMods!.Select(m => m.Package.Name));
    }

    /// <summary>
    /// The badge counts the same set the review window and the build do: a prebuilt pak's clash
    /// with an EXMOD mod shows without opening Review conflicts, and a pak before a mod in the queue
    /// is the earlier candidate.
    /// </summary>
    [Fact]
    public async Task ConflictBadge_CountsPrebuiltPaksInQueueOrder_LikeTheReviewWindow()
    {
        var (library, settings) = TwoConflictingMods();
        var pakFolder = CreateOpaquePakFolder("SomePak");
        library.FolderPaths["SomePak"] = pakFolder;
        var rebuild = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        rebuild.PrebuiltPakChanges[Path.Combine(pakFolder, "Prebuilt.pak")] =
            [new FieldChange("Crafting-D_ProcessorRecipes.json", "SmelterRecipe", "CraftTime", JsonValue.Create(3), JsonValue.Create(42), ValueSemantic.Scalar)];
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(MakeEntry("SomePak", isOpaquePak: true));
        vm.Queue.Add(library.AllEntries[0]);

        await vm.RecomputeConflictCountAsync();

        Assert.Equal(1, vm.ConflictCount);
        var review = vm.CreateConflictReviewForTesting();
        Assert.Equal(["Prebuilt", "ModA"], Assert.Single(review.AllConflicts).ModNames);
    }

    /// <summary>ISL's own merged pack can't be ticked: it would fold the old merge into the new one and apply the options twice.</summary>
    [Fact]
    public void AddToQueue_IslBuiltPack_IsRefusedWithAReason()
    {
        var builtPack = MakeEntry("zz_zz_ISL-Merged_9999_P", isOpaquePak: true);
        var importedPack = MakeEntry("SomeonesBuild", isOpaquePak: true);
        importedPack.MergedPackModNames = ["ModA"];
        var library = new FakeLibraryRepository { AllEntries = [builtPack, importedPack, MakeEntry("ModA")] };
        var vm = CreateViewModel(libraryRepository: library);

        vm.AddToQueueByFolderNames(["zz_zz_ISL-Merged_9999_P", "SomeonesBuild"]);

        Assert.Empty(vm.Queue);
        Assert.Contains("Merge & Install built", vm.StatusMessage);
        Assert.True(vm.IsIslBuiltPack(builtPack));
        Assert.False(vm.IsIslBuiltPack(library.AllEntries[2]));
    }

    /// <summary>
    /// A profile's mod that isn't in the Library stays in the profile (the auto-save writes it back),
    /// shows as a greyed row in its place, is left out of the build with a warning, and takes its
    /// place back once it's imported again.
    /// </summary>
    [Fact]
    public async Task MissingProfileMods_AreKept_ShownGreyed_AndSkippedByTheBuild()
    {
        var (library, settings) = TwoConflictingMods();
        library.AllEntries.RemoveAt(1); // ModB is gone from the Library
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["ModB", "ModA"] });
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: store, settingsService: settings);
        vm.SelectedProfileName = "Coop";

        Assert.Equal(["ModB", "ModA"], vm.Queue.Select(e => e.FolderName));
        Assert.True(vm.Queue[0].IsMissingFromLibrary);
        Assert.Contains("no longer in your Library", vm.ProfileStatusMessage);

        vm.RemoveLevelCap = true;
        vm.SaveWorkingState();
        Assert.Equal(["ModB", "ModA"], store.Load("Coop").MergeQueueFolderNames);

        Assert.True(await vm.RebuildAsync());
        Assert.Equal("ModA", Assert.Single(rebuild.LastQueuedMods!).Package.Name);
        Assert.Contains(vm.Warnings, w => w.Contains("'ModB'") && w.Contains("NOT part of this build"));

        // Imported again: the row is the real mod, in the same place.
        library.AllEntries.Add(MakeEntry("ModB"));
        _messenger.Send(new LibraryChangedMessage());
        Assert.False(vm.Queue[0].IsMissingFromLibrary);
        Assert.Equal(["ModB", "ModA"], vm.Queue.Select(e => e.FolderName));
    }

    /// <summary>
    /// Winners are kept by folder name: one saved by display name moves to the folder of the one
    /// mod with that name, and a pick still means that mod when another queued mod has the same
    /// display name. Winners for mods gone from the Library are dropped.
    /// </summary>
    [Fact]
    public async Task SavedWinners_MoveToFolderNames_AndPickTheRightModAmongSameNames()
    {
        var (library, settings) = TwoConflictingMods(nameA: "Shared Name", nameB: "Other");
        var store = new FakeProfileStore();
        store.Save(new Profile
        {
            Name = "Default",
            MergeQueueFolderNames = ["ModA", "ModB"],
            ConflictWinners = new()
            {
                [CraftTimeWinnerKey] = "Shared Name",
                ["crafting-d_other.json|Row|Field"] = "A mod that's gone",
            },
        });
        var rebuild = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: store, settingsService: settings);

        Assert.Equal("ModA", vm.WinnersForTesting[CraftTimeWinnerKey]);
        Assert.Single(vm.WinnersForTesting);
        Assert.Contains("1 saved conflict pick(s)", vm.ProfileStatusMessage);

        // ModB renamed to the same display name: the pick still means ModA (first in the queue).
        library.AllEntries[1].Name = "Shared Name";
        Assert.True(await vm.RebuildAsync());
        Assert.Equal(0, rebuild.LastManualPicks![CraftTimeKey]);
        Assert.DoesNotContain(vm.Warnings, w => w.Contains("no longer match"));
    }

    /// <summary>A saved pick for a mod in the build that no longer matches a conflict is reported before building.</summary>
    [Fact]
    public async Task SavedWinners_NoLongerMatching_AreReported()
    {
        var (library, settings) = TwoConflictingMods();
        library.FolderPaths["ModB"] = CreateExmodFolder("ModB2", BuildCraftTimeExmodJson("ModB", craftTime: 5), out _); // agrees with ModA now
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Default", MergeQueueFolderNames = ["ModA", "ModB"], ConflictWinners = new() { [CraftTimeWinnerKey] = "ModB" } });
        var rebuild = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: store, settingsService: settings);

        Assert.True(await vm.RebuildAsync());

        Assert.Null(rebuild.LastManualPicks);
        Assert.Contains(vm.Warnings, w => w.StartsWith("1 saved conflict pick(s) no longer match"));
    }

    /// <summary>ArcFission's data pak is only merged in while ArcFission Library itself is installed and on.</summary>
    [Fact]
    public async Task Rebuild_ArcFissionPakWithoutTheLibrary_IsNotMergedIn()
    {
        var contentPath = Path.Combine(_tempRoot, "Content");
        var modsFolder = Path.Combine(contentPath, "Paks", "mods");
        Directory.CreateDirectory(modsFolder);
        File.WriteAllBytes(Path.Combine(modsFolder, "ZZ_ArcFission_Data_3_P.pak"), []);
        var (library, settings) = TwoConflictingMods();
        settings.Current.IcarusContentPath = contentPath;
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);

        Assert.Null(vm.ArcFissionDataPakPath());
        Assert.True(await vm.RebuildAsync());
        Assert.Empty(rebuild.LastLowPriorityPrebuiltPakFilePaths!);
    }

    /// <summary>
    /// Install's confirmation says what really happens: ArcFission's pak (library installed) isn't
    /// listed as removed, the backup count is the real one; a leftover pak (library gone) is
    /// offered for removal and goes when that's accepted.
    /// </summary>
    [Fact]
    public async Task Install_ConfirmationListsWhatReallyGoes_AndOffersToRemoveALeftoverArcFissionPak()
    {
        var contentPath = Path.Combine(_tempRoot, "Content");
        var modsFolder = Path.Combine(contentPath, "Paks", "mods");
        Directory.CreateDirectory(modsFolder);
        File.WriteAllBytes(Path.Combine(modsFolder, "ZZ_ArcFission_Data_3_P.pak"), []);
        File.WriteAllBytes(Path.Combine(modsFolder, "Stray_P.pak"), []);
        var (library, settings) = TwoConflictingMods();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService();
        var vm = CreateViewModel(libraryRepository: library, installService: install, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);

        // Library gone: asked about the leftover first; Yes removes it.
        var asked = new List<string>();
        vm.Confirm = (message, title) => { asked.Add($"{title}: {message}"); return true; };
        await vm.RebuildAndInstallCommand.ExecuteAsync(null);
        Assert.Equal(2, asked.Count);
        Assert.StartsWith("Leftover ArcFission data", asked[0]);
        Assert.Contains("ZZ_ArcFission_Data_3_P.pak", asked[1]);
        Assert.Contains($"last {BackupCleanup.MergedPakBackupsKept} kept", asked[1]);
        Assert.False(install.LastKeepArcFissionDataPak);

        // Library installed: nothing to ask about it, and it isn't listed as removed.
        InstallArcFissionLibrary(contentPath);
        asked.Clear();
        await vm.RebuildAndInstallCommand.ExecuteAsync(null);
        Assert.Single(asked);
        Assert.DoesNotContain("ZZ_ArcFission_Data_3_P.pak", asked[0]);
        Assert.Contains("Stray_P.pak", asked[0]);
        Assert.True(install.LastKeepArcFissionDataPak);
    }

    /// <summary>Remove from game refuses while Icarus runs, before asking anything.</summary>
    [Fact]
    public async Task RemoveFromGame_IcarusRunning_RefusesWithoutAsking()
    {
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = Path.Combine(_tempRoot, "Content");
        var install = new FakeInstallService();
        var vm = CreateViewModel(installService: install, settingsService: settings, gameRunning: true);
        vm.Confirm = (_, _) => throw new InvalidOperationException("Must not ask.");

        await vm.RemoveFromGameCommand.ExecuteAsync(null);

        Assert.Equal(0, install.RemoveCallCount);
        Assert.Contains("Icarus is running", vm.InstallStatusMessage);
    }

    /// <summary>Restore previous install asks first, then puts the backup back.</summary>
    [Fact]
    public async Task RestorePreviousInstall_AsksThenRestores()
    {
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = Path.Combine(_tempRoot, "Content");
        var install = new FakeInstallService();
        var vm = CreateViewModel(installService: install, settingsService: settings);

        await vm.RestorePreviousInstallCommand.ExecuteAsync(null);
        Assert.Contains("no earlier install", vm.InstallStatusMessage);

        install.Previous = new PreviousInstall("old.pak", "old.txt", DateTime.UtcNow);
        vm.Confirm = (_, _) => false;
        await vm.RestorePreviousInstallCommand.ExecuteAsync(null);
        Assert.Equal(0, install.RestoreCallCount);

        vm.Confirm = (_, _) => true;
        await vm.RestorePreviousInstallCommand.ExecuteAsync(null);
        Assert.Equal(1, install.RestoreCallCount);
        Assert.Contains("Restored", vm.InstallStatusMessage);
    }

    /// <summary>
    /// Importing a patch saved under the profile already in use loads it: the page used to keep the
    /// queue from before (setting the selection to the same name changes nothing), and its next
    /// auto-save wrote that old queue back over the imported profile.
    /// </summary>
    [Fact]
    public async Task ImportPatch_UnderTheProfileInUse_LoadsTheImportAndKeepsIt()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB")] };
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["ModA"] });
        var patches = new FakePatchService
        {
            ImportResult = new PatchImportContents(
                new PatchManifest
                {
                    ProfileName = "Coop",
                    Mods = [new PatchModEntry { FolderName = "Theirs", Name = "ModB", Author = "Test", Version = "1", Bundled = false }],
                },
                new Dictionary<string, ExmodPackageContents>()),
        };
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, patchService: patches);
        vm.SelectedProfileName = "Coop";
        vm.UnlimitedAmmo = true; // a pending auto-save of Coop
        vm.PickPatchFile = () => "patch.json";

        await vm.ImportPatchCommand.ExecuteAsync(null);
        vm.SaveWorkingState();

        Assert.Equal(["ModB"], vm.Queue.Select(e => e.FolderName));
        Assert.Equal(["ModB"], store.Load("Coop").MergeQueueFolderNames);
        Assert.False(store.Load("Coop").Options.UnlimitedAmmo);
        Assert.StartsWith("Imported 'Coop'", vm.PatchStatusMessage);
    }

    /// <summary>Copy to game while a build runs would copy the pak the build is still writing: it's refused with a reason.</summary>
    [Fact]
    public async Task CopyToGame_WhileBuilding_IsRefused()
    {
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = Path.Combine(_tempRoot, "Content");
        var install = new FakeInstallService();
        var vm = CreateViewModel(installService: install, settingsService: settings);
        vm.Confirm = (_, _) => true;
        vm.IsRebuilding = true;

        await vm.CopyToGameCommand.ExecuteAsync(null);
        await vm.RemoveFromGameCommand.ExecuteAsync(null);
        await vm.RestorePreviousInstallCommand.ExecuteAsync(null);

        Assert.Equal(0, install.InstallCallCount);
        Assert.Equal(0, install.RemoveCallCount);
        Assert.Equal(0, install.RestoreCallCount);
        Assert.Contains("building", vm.InstallStatusMessage);
    }

    /// <summary>
    /// The pak is in the game before the Library copy is made: that copy failing is a note on a
    /// successful install, not "Install failed" (which also left the install unrecorded).
    /// </summary>
    [Fact]
    public async Task Install_LibraryCopyFails_StillReportsTheInstall()
    {
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = Path.Combine(_tempRoot, "Content");
        var library = new FakeLibraryRepository { ImportPakException = new IOException("folder is locked") };
        var install = new FakeInstallService();
        var vm = CreateViewModel(libraryRepository: library, installService: install, settingsService: settings);
        vm.Confirm = (_, _) => true;

        var outcome = await vm.InstallAsync();

        Assert.Equal(MergeInstallViewModel.InstallOutcome.Installed, outcome);
        Assert.Equal(1, install.InstallCallCount);
        Assert.True(vm.HasExistingInstall);
        Assert.NotNull(settings.Current.LastInstallAtUtc);
        Assert.StartsWith("Installed to", vm.InstallStatusMessage);
        Assert.Contains("folder is locked", vm.InstallStatusMessage);
    }

    /// <summary>Suggest order asks through the same seam as every other question, and applying it saves into the profile in use.</summary>
    [Fact]
    public async Task SuggestQueueOrder_Yes_AppliesAndSavesIntoTheProfileInUse()
    {
        var libraryRepository = new FakeLibraryRepository { AllEntries = [MakeEntry("SmallTweak"), MakeEntry("BigOverhaul")] };
        libraryRepository.FolderPaths["BigOverhaul"] = CreateExmodFolder("BigOverhaul", BuildBigOverhaulJson(), out _);
        libraryRepository.FolderPaths["SmallTweak"] = CreateExmodFolder("SmallTweak", BuildSmallTweakJson(), out _);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildBaseTables() };
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["SmallTweak", "BigOverhaul"] });
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService, profileStore: store);
        vm.SelectedProfileName = "Coop";
        string? asked = null;
        vm.Confirm = (message, _) => { asked = message; return true; };

        await vm.SuggestQueueOrderCommand.ExecuteAsync(null);

        Assert.Contains("save it to 'Coop'", asked);
        Assert.Equal(["BigOverhaul", "SmallTweak"], vm.Queue.Select(e => e.FolderName));
        Assert.Equal(["BigOverhaul", "SmallTweak"], store.Load("Coop").MergeQueueFolderNames);
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
        public string? LastMergedPackProfileName { get; private set; }

        /// <summary>Thrown by ImportPak (the built pack's Library copy) when set.</summary>
        public Exception? ImportPakException { get; set; }

        public IReadOnlyList<LibraryEntry> GetAll() => AllEntries;

        public IReadOnlyList<string> UnreadableFolders => [];

        public IReadOnlyList<LibraryEntry> Search(string query) => throw new NotSupportedException("Not exercised by these tests.");

        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public LibraryEntry ImportPaks(IReadOnlyList<string> pakFilePaths, string name, string? source = null, int? nexusModId = null, string? catalogEntryId = null) => throw new NotSupportedException("Not exercised by these tests.");

        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null)
        {
            ImportPakCallCount++;
            LastImportPakPath = pakFilePath;
            LastMergedPackProfileName = mergedPackProfileName;
            if (ImportPakException is not null)
            {
                throw ImportPakException;
            }

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
        public IReadOnlyList<string>? LastPrebuiltPakFilePaths { get; private set; }
        public IReadOnlyList<string>? LastLowPriorityPrebuiltPakFilePaths { get; private set; }
        public IReadOnlyList<int>? LastPrebuiltPakQueuePositions { get; private set; }
        public GameplayOptions? LastGameplayOptions { get; private set; }

        /// <summary>Runs inside RebuildAsync, before it returns — a test edits the page here, as a user would mid-build.</summary>
        public Func<Task>? DuringRebuild { get; set; }
        public IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? LastManualPicks { get; private set; }
        public RebuildResult Result { get; set; } = new(0, 0, "out.pak", "out.manifest", [], []);
        public Exception? ExceptionToThrow { get; set; }
        public Func<IEnumerable<string>, string, MergeReport, IReadOnlyDictionary<string, JsonObject>>? ReadKeyedBaseTablesHandler { get; set; }

        public Task<RebuildResult> RebuildAsync(
            IReadOnlyList<ExmodPackageContents> queuedMods, GameplayOptions gameplayOptions, string dataFolder, string unrealPakExePath,
            string outputPakPath, IReadOnlyList<string> prebuiltPakFilePaths,
            IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? manualPicks = null,
            IProgress<RebuildStageProgress>? progress = null, CancellationToken cancellationToken = default,
            IReadOnlyList<string>? lowPriorityPrebuiltPakFilePaths = null, IReadOnlyList<int>? prebuiltPakQueuePositions = null)
        {
            RebuildCallCount++;
            LastQueuedMods = queuedMods;
            LastPrebuiltPakFilePaths = prebuiltPakFilePaths;
            LastLowPriorityPrebuiltPakFilePaths = lowPriorityPrebuiltPakFilePaths;
            LastPrebuiltPakQueuePositions = prebuiltPakQueuePositions;
            LastManualPicks = manualPicks;
            LastGameplayOptions = gameplayOptions;
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return RunAsync();

            async Task<RebuildResult> RunAsync()
            {
                if (DuringRebuild is not null)
                {
                    await DuringRebuild();
                }

                return Result;
            }
        }

        /// <summary>Per pak path, the changes ComputePrebuiltPakFieldChangesAsync reports for it (none by default).</summary>
        public Dictionary<string, IReadOnlyList<FieldChange>> PrebuiltPakChanges { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<(string PakName, IReadOnlyList<FieldChange> Changes)>> ComputePrebuiltPakFieldChangesAsync(
            IReadOnlyList<string> prebuiltPakFilePaths, string dataFolder, string unrealPakExePath, MergeReport report,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<(string PakName, IReadOnlyList<FieldChange> Changes)>>(
                [.. prebuiltPakFilePaths
                    .Where(PrebuiltPakChanges.ContainsKey)
                    .Select(path => (Path.GetFileNameWithoutExtension(path), PrebuiltPakChanges[path]))]);

        public IReadOnlyDictionary<string, JsonObject> ReadKeyedBaseTables(IEnumerable<string> currentFiles, string dataFolder, MergeReport report) =>
            ReadKeyedBaseTablesHandler?.Invoke(currentFiles, dataFolder, report)
                ?? throw new NotSupportedException("Not exercised by these tests — configure ReadKeyedBaseTablesHandler if a test needs real base-table data.");
    }

    private sealed class FakeInstallService : IInstallService
    {
        public int InstallCallCount { get; private set; }
        public bool? LastKeepArcFissionDataPak { get; private set; }
        public InstallResult Result { get; set; } = new("installed.pak", null);

        public Task<InstallResult> InstallAsync(string stagedPakPath, string icarusContentPath, string backupDirectory, CancellationToken cancellationToken = default, bool keepArcFissionDataPak = true)
        {
            InstallCallCount++;
            LastKeepArcFissionDataPak = keepArcFissionDataPak;
            return Task.FromResult(Result);
        }

        public int RemoveCallCount { get; private set; }

        public PreviousInstall? Previous { get; set; }

        public int RestoreCallCount { get; private set; }

        public Task<PreviousInstall?> FindPreviousInstallAsync(string backupDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Previous);

        public Task<PreviousInstall?> RestorePreviousInstallAsync(string stagedPakFileName, string icarusContentPath, string backupDirectory, CancellationToken cancellationToken = default)
        {
            RestoreCallCount++;
            return Task.FromResult(Previous);
        }

        public IReadOnlyList<string> InstalledModNames { get; set; } = [];

        public Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstalledState(InstalledModNames));

        public Task<bool> RemoveInstalledPakAsync(string stagedPakFileName, string icarusContentPath, string backupDirectory, CancellationToken cancellationToken = default)
        {
            RemoveCallCount++;
            return Task.FromResult(false);
        }
    }

    private sealed class FakeProfileStore : IProfileStore
    {
        private readonly Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> ProfileNames => [.. _profiles.Keys];

        public Profile Load(string name) => _profiles[name];

        public void Save(Profile profile) => _profiles[profile.Name] = profile;

        public void Delete(string name) => _profiles.Remove(name);

        public void Rename(string oldName, string newName)
        {
            var profile = _profiles[oldName];
            _profiles.Remove(oldName);
            profile.Name = newName;
            _profiles[newName] = profile;
        }

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
        /// <summary>What ImportAsync reads from any patch file.</summary>
        public PatchImportContents? ImportResult { get; set; }

        public Task ExportAsync(PatchManifest manifest, IReadOnlyDictionary<string, ExmodPackageContents> bundledMods, string outputPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<PatchImportContents> ImportAsync(string patchFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImportResult ?? throw new NotSupportedException("Set ImportResult first."));
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
