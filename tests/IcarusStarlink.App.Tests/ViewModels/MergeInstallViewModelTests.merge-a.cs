using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.Install;

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

    /// <summary>Seen in a player's log: "Rebuild failed: No library entry named 'MkHaters_Range'" — one vanished folder failed the whole build.</summary>
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
}
