using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Rebuild;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Merge &amp; Install: where the installed merged pak is looked for, queued mods whose folder is
/// gone, profiles that can't be loaded, renamed mods against the game's list, profile renames,
/// an empty Update install, and Check the merge while the profile changes.
/// </summary>
public sealed partial class MergeInstallViewModelTests
{
    private IcarusStarlink.Storage.Profiles.ProfileStore RealProfileStore() =>
        new(Path.Combine(_tempRoot, "Profiles"), Path.Combine(_tempRoot, "ProfileBackups"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IcarusStarlink.Storage.Profiles.ProfileStore>.Instance);

    /// <summary>A game Content folder with an empty Paks\mods (no ue4ss folder, so no ArcFission Library).</summary>
    private (string ContentPath, string ModsFolder) MakeGameContent()
    {
        var contentPath = Path.Combine(_tempRoot, "Content");
        var modsFolder = Path.Combine(contentPath, "Paks", "mods");
        Directory.CreateDirectory(modsFolder);
        return (contentPath, modsFolder);
    }

    // ---------------------------------------------------------------------------------------
    // The installed merged pak goes by the name Install gives it in this game, not the staged name
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Install writes the merge as ISL-Merged_P.pak in a game without ArcFission Library. The
    /// "Changed since install" check used to look for the staged name there, find nothing, and
    /// never say a mod changed.
    /// </summary>
    [Fact]
    public async Task Rebuild_AModChangedWhileBuilding_ReadsChangedAfterInstall_UnderTheNameInstallWrites()
    {
        var (library, settings) = TwoConflictingMods();
        var (contentPath, modsFolder) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        rebuild.DuringRebuild = () =>
        {
            File.WriteAllText(Path.Combine(library.FolderPaths["ModA"], "notes.txt"), "edited mid-build");
            return Task.CompletedTask;
        };

        Assert.True(await vm.RebuildAsync());
        var installedName = InstallManifestNames.InstalledMergedPakFileName(contentPath);
        Assert.Equal("ISL-Merged_P.pak", installedName);
        File.Copy(Path.Combine(_tempRoot, "Staged", "IcarusStarlink-Merged.pak"), Path.Combine(modsFolder, installedName), overwrite: true);
        await vm.RefreshChangedSinceInstallAsync();

        Assert.True(vm.ChangedSinceInstall.TryGetValue("ModA", out var change), "ModA should read as changed since install");
        Assert.Equal("files changed", change);
    }

    /// <summary>ArcFission Library turned on after the install: the pak is still under its earlier name, and is still found.</summary>
    [Fact]
    public async Task ChangedSinceInstall_ThePakUnderItsOtherName_IsStillFound()
    {
        var (library, settings) = TwoConflictingMods();
        var (contentPath, modsFolder) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        rebuild.DuringRebuild = () =>
        {
            File.WriteAllText(Path.Combine(library.FolderPaths["ModA"], "notes.txt"), "edited mid-build");
            return Task.CompletedTask;
        };

        Assert.True(await vm.RebuildAsync());
        File.Copy(Path.Combine(_tempRoot, "Staged", "IcarusStarlink-Merged.pak"), Path.Combine(modsFolder, MergedPakNames.PlainName), overwrite: true);
        InstallArcFissionLibrary(contentPath);
        await vm.RefreshChangedSinceInstallAsync();

        Assert.True(vm.ChangedSinceInstall.TryGetValue("ModA", out var change), "ModA should read as changed since install");
        Assert.Equal("files changed", change);
    }

    /// <summary>Every re-install used to list Starlink's own installed pak as "1 other file(s) already there".</summary>
    [Fact]
    public async Task Install_WithStarlinksOwnPakInTheGame_DoesNotListItAsAnotherFile()
    {
        var settings = new FakeSettingsService();
        var (contentPath, modsFolder) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        File.WriteAllText(Path.Combine(modsFolder, MergedPakNames.PlainName), "installed");
        File.WriteAllText(Path.Combine(modsFolder, InstallManifestNames.PakManifest), "ModA");
        var install = new FakeInstallService();
        var vm = CreateViewModel(installService: install, settingsService: settings);
        string? message = null;
        _dialogs.OnConfirm = (text, title) =>
        {
            if (title == "Install to Icarus")
            {
                message = text;
            }

            return false;
        };

        await vm.InstallAsync();

        Assert.NotNull(message);
        Assert.DoesNotContain("ISL-Merged_P.pak", message);
        Assert.DoesNotContain("other file(s)", message);
        Assert.Equal(0, install.InstallCallCount);
    }

    /// <summary>The player's own merge in the game isn't "another file": a finder round used to ask "Install to Icarus?" every time because of it.</summary>
    [Fact]
    public async Task FindBrokenRound_WithThePlayersMergeInTheGame_DoesNotAsk()
    {
        var (vm, _, _, install, modsFolder) = FinderSetup();
        File.WriteAllText(Path.Combine(modsFolder, MergedPakNames.PlainName), "installed");
        File.WriteAllText(Path.Combine(modsFolder, InstallManifestNames.PakManifest), "ModA");
        var asked = 0;
        _dialogs.OnConfirm = (_, title) =>
        {
            asked += title == "Install to Icarus" ? 1 : 0;
            return true;
        };

        var failed = await vm.InstallForFinderAsync(["ModB"], backUpWhatsThere: false);

        Assert.Null(failed);
        Assert.Equal(0, asked);
        Assert.Equal(1, install.InstallCallCount);
    }

    /// <summary>A merged pak left under the ArcFission name in a game without ArcFission Library is Starlink's own, replaced (and backed up) by the install: the question says so.</summary>
    [Fact]
    public async Task Install_AMergedPakUnderAnEarlierName_IsNamedAsReplaced()
    {
        var settings = new FakeSettingsService();
        var (contentPath, modsFolder) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        File.WriteAllText(Path.Combine(modsFolder, MergedPakNames.ArcFissionName), "old install");
        var vm = CreateViewModel(settingsService: settings);
        string? message = null;
        _dialogs.OnConfirm = (text, title) =>
        {
            if (title == "Install to Icarus")
            {
                message = text;
            }

            return false;
        };

        await vm.InstallAsync();

        Assert.NotNull(message);
        Assert.Contains(MergedPakNames.ArcFissionName, message);
        Assert.Contains("earlier name", message);
        Assert.DoesNotContain("other file(s)", message);
    }

    // ---------------------------------------------------------------------------------------
    // A queued mod whose folder is gone
    // ---------------------------------------------------------------------------------------

    /// <summary>Regression: a queued mod whose folder is gone (renamed in Explorer, the Library not refreshed yet) failed the whole build with "No library entry named …"; it is now left out with a warning.</summary>
    [Fact]
    public async Task Rebuild_AQueuedModWhoseFolderIsGone_IsLeftOutWithAWarning()
    {
        var (library, settings) = TwoConflictingMods();
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        vm.Queue.Add(library.AllEntries[1]);
        // Renamed in Explorer; the Library hasn't refreshed yet, so it still lists the mod.
        library.FolderPaths.Remove("ModB");

        Assert.True(await vm.RebuildAsync(), vm.StatusMessage);

        Assert.Equal(["ModA"], rebuild.LastQueuedMods!.Select(m => m.Package.Name));
        Assert.Contains(vm.Warnings, w => w.Contains("'ModB'") && w.Contains("NOT part of this build"));
        // What the build was made from is still recorded, for "Changed since install".
        var record = Assert.Single(MergedModFingerprints.Load(Path.Combine(_tempRoot, "Staged", MergedModFingerprints.FileName)));
        Assert.Equal(["ModA"], record.Mods.Select(m => m.FolderName));
    }

    /// <summary>An opaque pak mod whose folder is gone is left out, not a failed build.</summary>
    [Fact]
    public async Task Rebuild_AQueuedPakModWhoseFolderIsGone_IsLeftOut()
    {
        var (library, settings) = TwoConflictingMods();
        var pakFolder = CreateOpaquePakFolder("PakMod");
        Directory.Delete(pakFolder, recursive: true);
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        vm.Queue.Add(MakeEntry("PakMod", isOpaquePak: true));

        Assert.True(await vm.RebuildAsync(), vm.StatusMessage);

        Assert.Empty(rebuild.LastPrebuiltPakFilePaths!);
        Assert.Contains(vm.Warnings, w => w.Contains("'PakMod'") && w.Contains("NOT part of this build"));
    }

    /// <summary>Suggest order used to fault on a gone folder (an [FTL] line in the log) and do nothing.</summary>
    [Fact]
    public async Task SuggestOrder_AQueuedModWhoseFolderIsGone_DoesNotThrow()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["BigOverhaul"] = CreateExmodFolder("BigOverhaul", BuildBigOverhaulJson(), out _);
        libraryRepository.FolderPaths["StaleCopy"] = CreateExmodFolder("StaleCopy", BuildStaleCopyJson(), out _);
        libraryRepository.FolderPaths["SmallTweak"] = CreateExmodFolder("SmallTweak", BuildSmallTweakJson(), out _);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);
        vm.Queue.Add(MakeEntry("BigOverhaul"));
        vm.Queue.Add(MakeEntry("StaleCopy"));
        vm.Queue.Add(MakeEntry("SmallTweak"));
        libraryRepository.FolderPaths.Remove("StaleCopy");
        _dialogs.OnConfirm = (_, _) => false;

        await vm.SuggestQueueOrderCommand.ExecuteAsync(null);

        Assert.NotNull(vm.StatusMessage);
    }

    /// <summary>Deleting a ticked mod in the Library left a normal row behind, and every Install failed until it was removed by hand.</summary>
    [Fact]
    public async Task DeletingATickedModInTheLibrary_GreysItsRow_AndTheBuildLeavesItOut()
    {
        var (library, settings) = TwoConflictingMods();
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        vm.Queue.Add(library.AllEntries[1]);

        library.AllEntries.RemoveAll(e => e.FolderName == "ModB");
        library.FolderPaths.Remove("ModB");
        _messenger.Send(new LibraryChangedMessage());

        Assert.Equal(["ModA", "ModB"], vm.Queue.Select(e => e.FolderName));
        Assert.False(vm.Queue[0].IsMissingFromLibrary);
        Assert.True(vm.Queue[1].IsMissingFromLibrary);
        Assert.True(await vm.RebuildAsync(), vm.StatusMessage);
        Assert.Equal(["ModA"], rebuild.LastQueuedMods!.Select(m => m.Package.Name));
    }

    /// <summary>A greyed row whose mod is imported again takes the real entry back, in place.</summary>
    [Fact]
    public void AGreyedRowWhoseModIsBack_TakesItsPlaceAgain()
    {
        var (library, settings) = TwoConflictingMods();
        var vm = CreateViewModel(libraryRepository: library, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        vm.Queue.Add(library.AllEntries[1]);
        var modB = library.AllEntries[1];
        library.AllEntries.Remove(modB);
        _messenger.Send(new LibraryChangedMessage());
        Assert.True(vm.Queue[1].IsMissingFromLibrary);

        library.AllEntries.Add(modB);
        _messenger.Send(new LibraryChangedMessage());

        Assert.Same(modB, vm.Queue[1]);
    }

    // ---------------------------------------------------------------------------------------
    // A profile that can't be loaded is never saved over
    // ---------------------------------------------------------------------------------------

    private const string BrokenCoopJson = """{ "Name": "Coop", "MergeQueueFolderNames": ["ModB",] }""";

    private string WriteBrokenCoop()
    {
        var path = Path.Combine(_tempRoot, "Profiles", "Coop.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BrokenCoopJson);
        return path;
    }

    private static FakeLibraryRepository ThreeMods() => new() { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB"), MakeEntry("ModC")] };

    /// <summary>
    /// Picking a profile with a stray comma left the previous profile's list on screen, and the
    /// next tick auto-saved that list into the broken profile's file, with no backup.
    /// </summary>
    [Fact]
    public void PickingAProfileThatCantLoad_NeverSavesTheShownListIntoIt()
    {
        var store = RealProfileStore();
        store.Save(new Profile { Name = "Solo", MergeQueueFolderNames = ["ModA"] });
        var coopPath = WriteBrokenCoop();
        var settings = new FakeSettingsService();
        var vm = CreateViewModel(libraryRepository: ThreeMods(), profileStore: store, settingsService: settings);

        vm.SelectedProfileName = "Solo";
        vm.SelectedProfileName = "Coop";
        Assert.Contains("Couldn't load", vm.ProfileStatusMessage);

        vm.AddToQueueByFolderNames(["ModC"]);
        vm.SaveWorkingState();

        Assert.Equal(BrokenCoopJson, File.ReadAllText(coopPath));
        // The next start doesn't open on the broken profile again.
        Assert.Equal("Solo", settings.Current.LastMergeProfileName);
    }

    /// <summary>The same at launch: the last profile used can't be read, the queue is empty, and the first tick used to save over it.</summary>
    [Fact]
    public void StartingOnAProfileThatCantLoad_NeverSavesIntoIt()
    {
        var store = RealProfileStore();
        var coopPath = WriteBrokenCoop();
        var settings = new FakeSettingsService();
        settings.Current.LastMergeProfileName = "Coop";
        var vm = CreateViewModel(libraryRepository: ThreeMods(), profileStore: store, settingsService: settings);
        Assert.Equal("Coop", vm.SelectedProfileName);
        Assert.Empty(vm.Queue);

        vm.AddToQueueByFolderNames(["ModA"]);
        vm.SaveWorkingState();

        Assert.Equal(BrokenCoopJson, File.ReadAllText(coopPath));
    }

    /// <summary>Clear on a profile that didn't load would save the other profile's list into it first: refused, nothing asked.</summary>
    [Fact]
    public void ClearQueue_OnAProfileThatCantLoad_IsRefused()
    {
        var store = RealProfileStore();
        store.Save(new Profile { Name = "Solo", MergeQueueFolderNames = ["ModA"] });
        var coopPath = WriteBrokenCoop();
        var vm = CreateViewModel(libraryRepository: ThreeMods(), profileStore: store);
        vm.SelectedProfileName = "Solo";
        vm.SelectedProfileName = "Coop";
        var asked = 0;
        _dialogs.OnConfirm = (_, _) =>
        {
            asked++;
            return true;
        };

        vm.ClearQueueCommand.Execute(null);

        Assert.Equal(0, asked);
        Assert.Equal(BrokenCoopJson, File.ReadAllText(coopPath));
        Assert.Contains("'Coop'", vm.StatusMessage);
        Assert.Single(vm.Queue);
    }

    /// <summary>Save profile on the broken profile is the player's own choice: it backs up the broken file first, and auto-saves work again after it.</summary>
    [Fact]
    public void SaveProfile_OnAProfileThatCantLoad_KeepsTheBrokenFileAsABackup_AndAutoSavesAgain()
    {
        var store = RealProfileStore();
        store.Save(new Profile { Name = "Solo", MergeQueueFolderNames = ["ModA"] });
        WriteBrokenCoop();
        var vm = CreateViewModel(libraryRepository: ThreeMods(), profileStore: store);
        vm.SelectedProfileName = "Solo";
        vm.SelectedProfileName = "Coop";

        vm.SaveProfileCommand.Execute(null);
        vm.AddToQueueByFolderNames(["ModC"]);
        vm.SaveWorkingState();

        Assert.Contains(Directory.GetFiles(Path.Combine(_tempRoot, "ProfileBackups")), f => File.ReadAllText(f) == BrokenCoopJson);
        Assert.Equal(["ModA", "ModC"], store.Load("Coop").MergeQueueFolderNames);
    }

    // ---------------------------------------------------------------------------------------
    // A renamed mod against the game's list
    // ---------------------------------------------------------------------------------------

    private static string BetterBackpacksJson() => """
        {
            "name": "Better Backpacks", "author": "Test", "version": "1", "description": "D", "fileName": "BetterBackpacks",
            "Rows": [
                {"CurrentFile": "Crafting-D_ProcessorRecipes.json", "File_Items": [{"Name": "SmelterRecipe", "CraftTime": 5}]}
            ]
        }
        """;

    private (FakeLibraryRepository Library, LibraryEntry Entry) RenamedBetterBackpacks()
    {
        var entry = MakeEntry("BetterBackpacks");
        entry.Name = "BB";
        var library = new FakeLibraryRepository { AllEntries = [entry] };
        library.FolderPaths["BetterBackpacks"] = CreateExmodFolder("BetterBackpacks", BetterBackpacksJson(), out _);
        return (library, entry);
    }

    /// <summary>The game's list names an EXMOD by its package name; a mod renamed in the Library used to show as "+ BB" and "- Better Backpacks".</summary>
    [Fact]
    public async Task CompareToInstalled_ARenamedMod_ReadsUnchanged()
    {
        var (library, entry) = RenamedBetterBackpacks();
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = MakeGameContent().ContentPath;
        var install = new FakeInstallService { InstalledModNames = ["Better Backpacks"] };
        var vm = CreateViewModel(libraryRepository: library, installService: install, settingsService: settings);
        vm.Queue.Add(entry);

        await vm.CompareToInstalledCommand.ExecuteAsync(null);

        Assert.Equal(["=  Better Backpacks"], vm.ComparisonEntries);
    }

    /// <summary>Restore queue from game used to skip a mod renamed in the Library: its name isn't the one the game's list has.</summary>
    [Fact]
    public async Task RestoreQueueFromGame_ARenamedMod_IsTickedFromTheBuildRecord()
    {
        var (library, _) = RenamedBetterBackpacks();
        var settings = new FakeSettingsService();
        var (contentPath, modsFolder) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService { InstalledModNames = ["Better Backpacks"] };
        var vm = CreateViewModel(libraryRepository: library, installService: install, settingsService: settings);
        var installedPak = Path.Combine(modsFolder, MergedPakNames.PlainName);
        File.WriteAllBytes(installedPak, [1, 2, 3]);
        MergedModFingerprints.Add(Path.Combine(_tempRoot, "Staged", MergedModFingerprints.FileName),
            new MergedBuildRecord(MergedModFingerprints.HashFile(installedPak), DateTimeOffset.UtcNow, [new MergedModFingerprint("BetterBackpacks", "1", "x")]));

        await vm.RefreshInstalledModNamesAsync();

        Assert.Equal(1, vm.RestoreQueueFromGame());
        Assert.Equal(["BetterBackpacks"], vm.Queue.Select(e => e.FolderName));
    }

    // ---------------------------------------------------------------------------------------
    // Profile rename
    // ---------------------------------------------------------------------------------------

    /// <summary>Changing only the letter case of a profile's name used to be refused as "already exists".</summary>
    [Fact]
    public void RenameProfile_CaseOnly_IsAllowed()
    {
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop" });
        var vm = CreateViewModel(profileStore: store);
        vm.SelectedProfileName = "Coop";
        vm.PromptForProfileName = (_, _) => "COOP";

        vm.RenameProfileCommand.Execute(null);

        Assert.Equal("Renamed 'Coop' to 'COOP'.", vm.ProfileStatusMessage);
        Assert.Equal("COOP", vm.SelectedProfileName);
    }

    // ---------------------------------------------------------------------------------------
    // Update install with nothing ticked
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The Library says an unticked mod "Will be removed: Merge &amp; Install takes it out", but
    /// Update install with nothing ticked only said "Add at least one mod" and left the mods in the game.
    /// </summary>
    [Fact]
    public async Task UpdateInstall_NothingTicked_WithAMergeInTheGame_OffersToRemoveIt()
    {
        var settings = new FakeSettingsService();
        var (contentPath, modsFolder) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        File.WriteAllText(Path.Combine(modsFolder, MergedPakNames.PlainName), "installed");
        var rebuild = new FakeRebuildService();
        var install = new FakeInstallService();
        var vm = CreateViewModel(rebuildService: rebuild, installService: install, settingsService: settings);
        await vm.RefreshHasExistingInstallAsync();
        Assert.True(vm.HasExistingInstall);
        string? question = null;
        _dialogs.OnConfirm = (text, title) =>
        {
            question = text;
            return title == "Remove from Icarus";
        };

        await vm.RebuildAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(1, install.RemoveCallCount);
        Assert.Equal(0, rebuild.RebuildCallCount);
        Assert.StartsWith("Nothing is ticked, so there's nothing to build.", question);
    }

    // ---------------------------------------------------------------------------------------
    // Check the merge while the profile changes
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Switching profile while Check the merge reads the mods used to open the window with the old
    /// profile's conflicts, and Save picks then wrote the old profile's winners into the new one,
    /// dropping the new profile's own picks.
    /// </summary>
    [Fact]
    public async Task CheckTheMerge_ProfileSwitchedWhileChecking_SavesNothingIntoTheNewProfile()
    {
        var (library, settings) = TwoConflictingMods();
        var rebuild = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Solo", MergeQueueFolderNames = ["ModA", "ModB"], ConflictWinners = new() { [CraftTimeWinnerKey] = "ModA" } });
        store.Save(new Profile
        {
            Name = "Coop",
            MergeQueueFolderNames = ["ModA", "ModB"],
            ConflictWinners = new() { [CraftTimeWinnerKey] = "ModB", ["Other|Row|Field"] = "ModB" },
        });
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: store, settingsService: settings);
        vm.SelectedProfileName = "Solo";

        // Every read of a mod folder off this thread waits until the profile has been switched, so
        // the check can't finish first (this thread's own reads, if any, go through).
        using var gate = new ManualResetEventSlim(false);
        var testThread = Environment.CurrentManagedThreadId;
        var held = 0;
        library.BeforeGetFolderPath = _ =>
        {
            if (Environment.CurrentManagedThreadId != testThread)
            {
                Interlocked.Exchange(ref held, 1);
                gate.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var shown = false;
        vm.ShowMergeReview = review =>
        {
            shown = true;
            return review.BuildWinners();
        };

        var task = vm.ReviewMergeCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref held) == 1, 5000));
        vm.SelectedProfileName = "Coop";
        gate.Set();
        await task;

        Assert.False(shown);
        Assert.Equal("ModB", store.Load("Coop").ConflictWinners["Other|Row|Field"]);
        Assert.Equal("ModB", store.Load("Coop").ConflictWinners[CraftTimeWinnerKey]);
        Assert.Contains("changed while checking", vm.ConflictStatusMessage);
    }

    // ---------------------------------------------------------------------------------------
    // The conflict badge's game tables and pak diffs are read once, and option changes wait
    // ---------------------------------------------------------------------------------------

    /// <summary>Every conflict check used to read and parse every game table the queue touches again (24 MB for a big profile).</summary>
    [Fact]
    public async Task ConflictCheck_ReadsEachBaseTableOnce_UntilTheGameDataChanges()
    {
        var (library, settings) = TwoConflictingMods();
        var reads = 0;
        var rebuild = new FakeRebuildService
        {
            ReadKeyedBaseTablesHandler = (_, _, _) =>
            {
                Interlocked.Increment(ref reads);
                return BuildSmelterRecipeBaseTables();
            },
        };
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        vm.Queue.Add(library.AllEntries[1]);

        await vm.RecomputeConflictCountAsync();
        await vm.RecomputeConflictCountAsync();

        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(1, vm.ConflictCount);

        _messenger.Send(new WeeklyChangeReportUpdatedMessage());
        await vm.RecomputeConflictCountAsync();

        Assert.Equal(2, Volatile.Read(ref reads));
        Assert.Equal(1, vm.ConflictCount);
    }

    /// <summary>A boost change used to start a full conflict check at once, skipping the debounce (five of them on one profile switch).</summary>
    [Fact]
    public void ChangingABoost_RestartsTheDebounce_RatherThanRecountingInline()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsRecomputeDebouncePending);

        vm.SpeedBoost = BoostLevel.Level1;

        Assert.True(vm.IsRecomputeDebouncePending);
    }

    /// <summary>Two idle minutes used to throw away every prebuilt pak's diff, so the next change ran UnrealPak on each one again.</summary>
    [Fact]
    public async Task ReleaseIfIdle_KeepsThePrebuiltPakDiffs()
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
        Assert.Equal(1, rebuild.PrebuiltPakDiffCount);
        Assert.True(vm.ReleaseIfIdle(TimeSpan.Zero));
        await vm.RecomputeConflictCountAsync();

        Assert.Equal(1, rebuild.PrebuiltPakDiffCount);
        Assert.Equal(1, vm.ConflictCount);
    }

    // ---------------------------------------------------------------------------------------
    // The build's own Library import
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The build's own pak landing in the Library used to be handled like an outside change: every
    /// queued mod was read again and the conflict check run again. An edit that leaves a mod's
    /// fingerprint as it was shows that the cache survived.
    /// </summary>
    [Fact]
    public async Task Rebuild_ItsOwnLibraryImport_KeepsThePackageCache()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB")] };
        library.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        library.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeExmodJson("ModB", craftTime: 5), out var modBExmodPath);
        var rebuild = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var settings = new FakeSettingsService();
        settings.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        vm.Queue.Add(library.AllEntries[1]);
        await vm.RecomputeConflictCountAsync();
        Assert.Equal(0, vm.ConflictCount);
        var writeUtc = File.GetLastWriteTimeUtc(modBExmodPath);
        File.WriteAllText(modBExmodPath, BuildCraftTimeExmodJson("ModB", craftTime: 99));
        File.SetLastWriteTimeUtc(modBExmodPath, writeUtc);

        Assert.True(await vm.RebuildAsync());
        Assert.Equal(1, library.ImportPakCallCount);
        await vm.RecomputeConflictCountAsync();

        Assert.Equal(0, vm.ConflictCount);
    }

    /// <summary>After a restart, Copy to game used to import the staged pak into the Library again although the Library already had that same build.</summary>
    [Fact]
    public async Task CopyToGame_AfterARestart_DoesNotImportTheSameBuildAgain()
    {
        var settings = new FakeSettingsService();
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var built = MakeEntry("IcarusStarlink-Merged", isOpaquePak: true);
        built.MergedPackModNames = [];
        var library = new FakeLibraryRepository { AllEntries = [built] };
        var install = new FakeInstallService();
        var vm = CreateViewModel(libraryRepository: library, installService: install, settingsService: settings);
        var copyFolder = Path.Combine(_tempRoot, "Mods", "IcarusStarlink-Merged");
        Directory.CreateDirectory(copyFolder);
        File.Copy(Path.Combine(_tempRoot, "Staged", "IcarusStarlink-Merged.pak"), Path.Combine(copyFolder, "IcarusStarlink-Merged.pak"));
        library.FolderPaths["IcarusStarlink-Merged"] = copyFolder;
        _dialogs.OnConfirm = (_, _) => true;

        await vm.CopyToGameCommand.ExecuteAsync(null);

        Assert.Equal(1, install.InstallCallCount);
        Assert.Equal(0, library.ImportPakCallCount);
    }

    // ---------------------------------------------------------------------------------------
    // A Find the broken mod round isn't the player's build
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A round used to replace the Library's built pack with the round's half queue, and warn that
    /// saved picks "no longer match" because their partner mods were out for the round.
    /// </summary>
    [Fact]
    public async Task FindBrokenRound_IsNotFiledAsYourBuild()
    {
        var library = new FakeLibraryRepository();
        foreach (var (name, craftTime) in new[] { ("ModA", 5), ("ModB", 99), ("ModC", 7) })
        {
            library.FolderPaths[name] = CreateExmodFolder(name, BuildCraftTimeExmodJson(name, craftTime), out _);
            library.AllEntries.Add(MakeEntry(name));
        }

        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Default", MergeQueueFolderNames = ["ModA", "ModB", "ModC"], ConflictWinners = new() { [CraftTimeWinnerKey] = "ModA" } });
        var settings = new FakeSettingsService();
        settings.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var rebuild = new FakeRebuildService
        {
            Result = new RebuildResult(1, 1, "out.pak", "out.manifest", [], []),
            ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables(),
        };
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: store, settingsService: settings);
        Assert.Equal(3, vm.Queue.Count);

        Assert.Null(await vm.InstallForFinderAsync(["ModA"]));

        Assert.Equal(0, library.ImportPakCallCount);
        Assert.DoesNotContain(vm.Warnings, w => w.Contains("no longer match", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // Install's file errors say what went wrong
    // ---------------------------------------------------------------------------------------

    private (MergeInstallViewModel Vm, FakeInstallService Install) InstallSetup(Exception? installError = null, bool gameRunning = false)
    {
        var settings = new FakeSettingsService();
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService { InstallException = installError };
        var vm = CreateViewModel(installService: install, settingsService: settings, gameRunning: gameRunning);
        _dialogs.OnConfirm = (_, _) => true;
        return (vm, install);
    }

    /// <summary>A full drive used to read "the pak is in use … Close Icarus, then click Copy to game", which fails the same way.</summary>
    [Fact]
    public async Task Install_ADriveIsFull_SaysSo_NotThatThePakIsInUse()
    {
        var (vm, _) = InstallSetup(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.Failed, await vm.InstallAsync());

        Assert.Contains("full", vm.InstallStatusMessage);
        Assert.DoesNotContain("in use", vm.InstallStatusMessage);
        Assert.True(vm.CanCopyToGame);
    }

    /// <summary>The game's drive unplugged or the game moved: the Content folder setting is what to check.</summary>
    [Fact]
    public async Task Install_TheGameFolderIsGone_PointsAtTheContentFolderSetting()
    {
        var (vm, _) = InstallSetup(new DirectoryNotFoundException(@"Could not find a part of the path 'X:\Icarus\Content\Paks\mods'."));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.Failed, await vm.InstallAsync());

        Assert.Contains("Icarus Content folder", vm.InstallStatusMessage);
        Assert.DoesNotContain("in use", vm.InstallStatusMessage);
    }

    /// <summary>A sharing violation is the one case where a file in the mods folder really is in use.</summary>
    [Fact]
    public async Task Install_AFileInUse_SaysAFileInTheModsFolderIsInUse()
    {
        var (vm, _) = InstallSetup(new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020)));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.Failed, await vm.InstallAsync());

        Assert.Contains("a file in the game's mods folder is in use", vm.InstallStatusMessage);
        Assert.Contains("Copy to game", vm.InstallStatusMessage);
    }

    /// <summary>Refused because the game runs, right after a fresh build: Copy to game saves building again.</summary>
    [Fact]
    public async Task Install_WhileTheGameRuns_PointsAtCopyToGame()
    {
        var (vm, install) = InstallSetup(gameRunning: true);

        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, await vm.InstallAsync());

        Assert.Equal(0, install.InstallCallCount);
        Assert.Contains("Copy to game", vm.InstallStatusMessage);
    }

    /// <summary>Copy to game during a search would install the round's pak outside the finder: a round's error never suggests it.</summary>
    [Fact]
    public async Task FindBrokenRound_AFileInUse_DoesNotPointAtCopyToGame()
    {
        var (vm, _, _, install, _) = FinderSetup();
        install.InstallException = new IOException("locked", unchecked((int)0x80070020));

        var failed = await vm.InstallForFinderAsync(["ModA"]);

        Assert.NotNull(failed);
        Assert.DoesNotContain("Copy to game", failed);
    }

    // ---------------------------------------------------------------------------------------
    // Copy to game checks the staged pak is still current
    // ---------------------------------------------------------------------------------------

    /// <summary>Copy to game used to install a pak built before an Icarus update, putting the old game tables back.</summary>
    [Fact]
    public async Task CopyToGame_AfterAGameUpdate_IsRefused()
    {
        var settings = new FakeSettingsService();
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService();
        var vm = CreateViewModel(installService: install, settingsService: settings, gameDataFreshness: new FakeGameDataFreshness { Outdated = true });
        _dialogs.OnConfirm = (_, _) => true;

        await vm.CopyToGameCommand.ExecuteAsync(null);

        Assert.Equal(0, install.InstallCallCount);
        Assert.Contains("updated", vm.InstallStatusMessage);
    }

    /// <summary>A pak built before the last Refresh game data may hold old game tables.</summary>
    [Fact]
    public async Task CopyToGame_AfterRefreshGameData_IsRefused()
    {
        var settings = new FakeSettingsService();
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService();
        var vm = CreateViewModel(installService: install, settingsService: settings);
        settings.Current.LastDataFolderUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        _dialogs.OnConfirm = (_, _) => true;

        await vm.CopyToGameCommand.ExecuteAsync(null);

        Assert.Equal(0, install.InstallCallCount);
        Assert.Contains("refreshed", vm.InstallStatusMessage);
    }

    /// <summary>After a profile switch, Copy to game's question says which profile the staged pak was built from.</summary>
    [Fact]
    public async Task CopyToGame_AfterAProfileSwitch_NamesTheProfileThePakWasBuiltFrom()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA")] };
        library.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var settings = new FakeSettingsService();
        settings.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["ModA"] });
        store.Save(new Profile { Name = "Solo" });
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, settingsService: settings);
        vm.SelectedProfileName = "Coop";
        Assert.True(await vm.RebuildAsync());
        vm.SelectedProfileName = "Solo";
        string? question = null;
        _dialogs.OnConfirm = (text, _) =>
        {
            question = text;
            return false;
        };

        await vm.CopyToGameCommand.ExecuteAsync(null);

        Assert.Contains("built from profile 'Coop'", question);
    }

    // ---------------------------------------------------------------------------------------
    // Queue buttons and the merge option pills
    // ---------------------------------------------------------------------------------------

    /// <summary>Move up, Move down and Remove used to look enabled with nothing selected, and did nothing.</summary>
    [Fact]
    public void QueueButtons_NeedASelectedRow()
    {
        var vm = CreateViewModel();
        vm.Queue.Add(MakeEntry("ModA"));
        vm.Queue.Add(MakeEntry("ModB"));

        Assert.False(vm.RemoveFromQueueCommand.CanExecute(null));
        Assert.False(vm.MoveQueueEntryUpCommand.CanExecute(null));
        Assert.False(vm.MoveQueueEntryDownCommand.CanExecute(null));

        vm.SelectedQueueEntry = vm.Queue[0];
        Assert.True(vm.RemoveFromQueueCommand.CanExecute(null));
        Assert.False(vm.MoveQueueEntryUpCommand.CanExecute(null));
        Assert.True(vm.MoveQueueEntryDownCommand.CanExecute(null));

        vm.Queue.Move(0, 1);
        Assert.True(vm.MoveQueueEntryUpCommand.CanExecute(null));
        Assert.False(vm.MoveQueueEntryDownCommand.CanExecute(null));
    }

    /// <summary>
    /// A profile's Stacks x2.5 shows as Off, but clicking the highlighted Off pill changed no
    /// selection, so x2.5 stayed in the summary and every build until another pill was clicked first.
    /// </summary>
    [Fact]
    public void ClickingTheHighlightedPill_DropsAKeptMultiplier()
    {
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", Options = new GameplayOptions { StacksMultiplier = 2.5, SlotsMultiplier = 3 } });
        var vm = CreateViewModel(profileStore: store);
        vm.SelectedProfileName = "Coop";
        Assert.Equal(MultiplierLevel.Off, vm.StacksMultiplierLevel);
        Assert.Contains("Stacks x2.5", vm.ActiveOptionsSummary);

        vm.UsePillInsteadOfKeptMultiplier(slots: false);
        vm.SaveWorkingState();

        Assert.DoesNotContain("Stacks", vm.ActiveOptionsSummary);
        var saved = store.Load("Coop").Options;
        Assert.Null(saved.StacksMultiplier);
        Assert.Equal(3, saved.SlotsMultiplier);
    }

    // ---------------------------------------------------------------------------------------
    // Cancel a build
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A build couldn't be stopped: a hung UnrealPak kept the page on "Rebuilding…" with Install
    /// refused until it was killed by hand. Cancel stops it, and nothing is installed.
    /// </summary>
    [Fact]
    public async Task CancelDuringABuild_StopsIt_AndInstallsNothing()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA")] };
        library.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var settings = new FakeSettingsService();
        settings.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var rebuild = new FakeRebuildService();
        var install = new FakeInstallService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, installService: install, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        _dialogs.OnConfirm = (_, _) => true;
        Assert.False(vm.CancelBuildCommand.CanExecute(null));
        var couldCancel = false;
        rebuild.DuringRebuild = () =>
        {
            // UnrealPak is stopped when the token is cancelled (ProcessRunner); the fake throws as it would.
            couldCancel = vm.CancelBuildCommand.CanExecute(null);
            vm.CancelBuildCommand.Execute(null);
            rebuild.LastCancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await vm.RebuildAndInstallCommand.ExecuteAsync(null);

        Assert.True(couldCancel);
        Assert.Equal(1, rebuild.RebuildCallCount);
        Assert.Equal(0, install.InstallCallCount);
        Assert.Equal(0, library.ImportPakCallCount);
        Assert.Contains("cancelled", vm.StatusMessage);
        Assert.False(vm.IsRebuilding);
        Assert.False(vm.CancelBuildCommand.CanExecute(null));
    }

    /// <summary>
    /// Regression: a cancelled UnrealPak that died with a file error instead of an
    /// OperationCanceledException showed "Rebuild failed: …" though the player had pressed Cancel.
    /// </summary>
    [Fact]
    public async Task CancelDuringABuild_WhenThePackerDiesWithAFileError_StillSaysCancelled()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA")] };
        library.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var settings = new FakeSettingsService();
        settings.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var rebuild = new FakeRebuildService();
        var install = new FakeInstallService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, installService: install, settingsService: settings);
        vm.Queue.Add(library.AllEntries[0]);
        _dialogs.OnConfirm = (_, _) => true;
        rebuild.DuringRebuild = () =>
        {
            vm.CancelBuildCommand.Execute(null);
            throw new IOException("The pipe has been ended.");
        };

        await vm.RebuildAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(0, install.InstallCallCount);
        Assert.Contains("cancelled", vm.StatusMessage);
        Assert.DoesNotContain("failed", vm.StatusMessage);
        Assert.False(vm.CanCopyToGame);
        Assert.False(vm.IsRebuilding);
    }

    // ---------------------------------------------------------------------------------------
    // Profile failures reach the activity log; the page's own messenger carries its sends
    // ---------------------------------------------------------------------------------------

    /// <summary>The activity log every view model made by CreateViewModel writes to.</summary>
    private readonly FakeActivityLog _activityLog = new();

    /// <summary>A profile store whose writes fail once FailWrites is set (a full drive, a locked file).</summary>
    private sealed class FailingProfileStore : IProfileStore
    {
        private readonly FakeProfileStore _inner = new();

        public bool FailWrites { get; set; }

        public IReadOnlyList<string> ProfileNames => _inner.ProfileNames;

        public Profile Load(string name) => _inner.Load(name);

        public void Save(Profile profile)
        {
            if (FailWrites)
            {
                throw new IOException("There is not enough space on the disk.");
            }

            _inner.Save(profile);
        }

        public void Delete(string name) => _inner.Delete(name);

        public void Rename(string oldName, string newName) => _inner.Rename(oldName, newName);

        public bool HasBackup(string name) => true;

        public bool RestoreLatestBackup(string name) =>
            FailWrites ? throw new IOException("There is not enough space on the disk.") : false;
    }

    private (MergeInstallViewModel Vm, FailingProfileStore Store) VmWithFailingProfileWrites()
    {
        var store = new FailingProfileStore();
        store.Save(new Profile { Name = "Default" });
        var vm = CreateViewModel(profileStore: store);
        vm.SelectedProfileName = "Default";
        store.FailWrites = true;
        return (vm, store);
    }

    /// <summary>
    /// Save profile failing (full drive) used to show only on the page's status line: the activity
    /// log, which stays after leaving the page, listed the successes but never the failure.
    /// </summary>
    [Fact]
    public void SaveProfile_WhenTheWriteFails_AlsoLogsAWarning()
    {
        var (vm, _) = VmWithFailingProfileWrites();

        vm.SaveProfileCommand.Execute(null);

        Assert.StartsWith("Couldn't save profile", vm.ProfileStatusMessage);
        Assert.Contains(_activityLog.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message == vm.ProfileStatusMessage);
    }

    /// <summary>New profile failing to write is a Warning in the activity log too, not only the status line.</summary>
    [Fact]
    public void NewProfile_WhenTheWriteFails_AlsoLogsAWarning()
    {
        var (vm, _) = VmWithFailingProfileWrites();
        vm.PromptForProfileName = (_, _) => "Coop";

        vm.NewProfileCommand.Execute(null);

        Assert.StartsWith("Couldn't create profile", vm.ProfileStatusMessage);
        Assert.Contains(_activityLog.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message == vm.ProfileStatusMessage);
    }

    /// <summary>Restore profile backup failing is a Warning in the activity log too, not only the status line.</summary>
    [Fact]
    public void RestoreProfileBackup_WhenItFails_AlsoLogsAWarning()
    {
        var (vm, _) = VmWithFailingProfileWrites();
        _dialogs.OnConfirm = (_, title) => title == "Restore profile backup";

        vm.RestoreProfileBackupCommand.Execute(null);

        Assert.StartsWith("Couldn't restore profile", vm.ProfileStatusMessage);
        Assert.Contains(_activityLog.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message == vm.ProfileStatusMessage);
    }

    /// <summary>
    /// The empty list's Go to Library sends on the page's own messenger, like everything else on
    /// the page. It used to send on the app-wide default one, which a test (or a second window)
    /// listening on its own messenger never heard.
    /// </summary>
    [Fact]
    public void GoToLibrary_SendsTheNavigationOnThePagesOwnMessenger()
    {
        var vm = CreateViewModel();
        NavigateToPageMessage? sent = null;
        _messenger.Register<NavigateToPageMessage>(this, (_, message) => sent = message);

        vm.GoToLibraryCommand.Execute(null);

        Assert.Equal("library", sent?.NavItemId);
    }
}
