using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Update everything's install and build ask nothing mid-run: the yes is given before the run
/// starts. Their question used to open behind the Game update window while the user was in
/// another program, and the run sat on its Install step until Starlink was restarted.
/// </summary>
public sealed partial class MergeInstallViewModelTests
{
    private (MergeInstallViewModel Vm, FakeInstallService Install, string ModsFolder) AgreedInstallSetup()
    {
        var settings = new FakeSettingsService();
        var (contentPath, modsFolder) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService();
        return (CreateViewModel(installService: install, settingsService: settings), install, modsFolder);
    }

    /// <summary>The fake dialogs throw on any question, so installing at all proves none was asked.</summary>
    [Fact]
    public async Task Install_AgreedBeforehand_AsksNothing()
    {
        var (vm, install, _) = AgreedInstallSetup();

        var outcome = await vm.InstallAsync(new MergeInstallViewModel.InstallConsent(false, []));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.Installed, outcome);
        Assert.Equal(1, install.InstallCallCount);
    }

    [Fact]
    public async Task Install_AgreedBeforehand_RemovesTheOtherFilesThatWereListed()
    {
        var (vm, install, modsFolder) = AgreedInstallSetup();
        File.WriteAllText(Path.Combine(modsFolder, "SomeOtherMod_P.pak"), "x");

        Assert.Equal(["SomeOtherMod_P.pak"], vm.PreviewInstallExtras().OtherFiles);
        var outcome = await vm.InstallAsync(new MergeInstallViewModel.InstallConsent(false, ["SomeOtherMod_P.pak"]));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.Installed, outcome);
        Assert.Equal(1, install.InstallCallCount);
    }

    /// <summary>A file that turned up in the mods folder after Start was never shown: it isn't removed unseen, and nothing is asked.</summary>
    [Fact]
    public async Task Install_AgreedBeforehand_AFileThatWasntListed_IsNotInstalledOver()
    {
        var (vm, install, modsFolder) = AgreedInstallSetup();
        File.WriteAllText(Path.Combine(modsFolder, "Listed_P.pak"), "x");
        File.WriteAllText(Path.Combine(modsFolder, "CameLater_P.pak"), "x");

        var outcome = await vm.InstallAsync(new MergeInstallViewModel.InstallConsent(false, ["Listed_P.pak"]));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, outcome);
        Assert.Equal(0, install.InstallCallCount);
        Assert.Contains("CameLater_P.pak", vm.InstallStatusMessage);
        Assert.DoesNotContain("Listed_P.pak", vm.InstallStatusMessage);
        Assert.Contains("Copy to game", vm.InstallStatusMessage);
        Assert.True(File.Exists(Path.Combine(modsFolder, "CameLater_P.pak")));
    }

    [Fact]
    public async Task Install_AgreedBeforehand_StillRefusesWhileIcarusRuns()
    {
        var settings = new FakeSettingsService();
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService();
        var vm = CreateViewModel(installService: install, settingsService: settings, game: new FakeGameProcessChecker(running: true));

        var outcome = await vm.InstallAsync(new MergeInstallViewModel.InstallConsent(false, []));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, outcome);
        Assert.Equal(0, install.InstallCallCount);
    }

    /// <summary>ArcFission Library is gone and two of its data paks are left: the newest and one older copy.</summary>
    private static void LeaveArcFissionData(string modsFolder)
    {
        File.WriteAllText(Path.Combine(modsFolder, "ZZ_ArcFission_Data_2_P.pak"), "old");
        File.WriteAllText(Path.Combine(modsFolder, "ZZ_ArcFission_Data_3_P.pak"), "new");
    }

    /// <summary>
    /// Regression: with leftover ArcFission data the older copies weren't listed, yet keeping the
    /// leftover data (No at Start) still removes them, with no backup: files went that the window
    /// never named.
    /// </summary>
    [Fact]
    public void PreviewInstallExtras_LeftoverArcFissionData_StillListsItsOlderCopies()
    {
        var (vm, _, modsFolder) = AgreedInstallSetup();
        LeaveArcFissionData(modsFolder);

        var extras = vm.PreviewInstallExtras();

        Assert.Equal(["ZZ_ArcFission_Data_2_P.pak", "ZZ_ArcFission_Data_3_P.pak"], extras.LeftoverArcFission.Order());
        Assert.Equal(["ZZ_ArcFission_Data_2_P.pak"], extras.OldArcFissionCopies);
        Assert.Empty(extras.OtherFiles);
    }

    [Fact]
    public async Task Install_AgreedBeforehand_KeepTheLeftoverArcFissionData_KeepsIt_AndAsksNothing()
    {
        var (vm, install, modsFolder) = AgreedInstallSetup();
        LeaveArcFissionData(modsFolder);

        var outcome = await vm.InstallAsync(new MergeInstallViewModel.InstallConsent(RemoveLeftoverArcFission: false, []));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.Installed, outcome);
        Assert.True(install.LastKeepArcFissionDataPak);
    }

    [Fact]
    public async Task Install_AgreedBeforehand_RemoveTheLeftoverArcFissionData_RemovesIt_AndAsksNothing()
    {
        var (vm, install, modsFolder) = AgreedInstallSetup();
        LeaveArcFissionData(modsFolder);

        var outcome = await vm.InstallAsync(new MergeInstallViewModel.InstallConsent(
            RemoveLeftoverArcFission: true, ["ZZ_ArcFission_Data_2_P.pak", "ZZ_ArcFission_Data_3_P.pak"]));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.Installed, outcome);
        Assert.False(install.LastKeepArcFissionDataPak);
    }

    /// <summary>Yes to removing the leftover data, but its names not among the files agreed to: nothing is removed unseen.</summary>
    [Fact]
    public async Task Install_AgreedBeforehand_RemoveTheLeftoverData_WithoutItsNamesAgreed_IsNotInstalled()
    {
        var (vm, install, modsFolder) = AgreedInstallSetup();
        LeaveArcFissionData(modsFolder);

        var outcome = await vm.InstallAsync(new MergeInstallViewModel.InstallConsent(RemoveLeftoverArcFission: true, []));

        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, outcome);
        Assert.Equal(0, install.InstallCallCount);
    }

    [Fact]
    public void PreviewInstallExtras_AnEmptyModsFolder_HasNothingToList()
    {
        var (vm, _, _) = AgreedInstallSetup();

        var extras = vm.PreviewInstallExtras();

        Assert.False(extras.Any);
        Assert.Null(GameUpdateCoordinator.DescribeInstallExtras(extras));
    }

    [Fact]
    public void DescribeInstallExtras_NamesTheFiles_AndSaysTheyAreAskedAboutAtStart()
    {
        var text = GameUpdateCoordinator.DescribeInstallExtras(new MergeInstallViewModel.InstallExtras([], ["A_P.pak", "B_P.pak"], []));

        Assert.Contains("2 other files", text);
        Assert.Contains("A_P.pak, B_P.pak", text);
        Assert.Contains("backed up first", text);
        Assert.Contains("when you click Start", text);
    }

    [Fact]
    public void DescribeInstallExtras_LeftoverArcFissionData_SaysKeepingItStillRemovesTheOlderCopies()
    {
        var text = GameUpdateCoordinator.DescribeInstallExtras(new MergeInstallViewModel.InstallExtras(
            ["ZZ_ArcFission_Data_2_P.pak", "ZZ_ArcFission_Data_3_P.pak"], [], ["ZZ_ArcFission_Data_2_P.pak"]));

        Assert.Contains("If you keep it, Install still removes its older copy, not backed up (ZZ_ArcFission_Data_2_P.pak)", text);
    }

    private static readonly GameUpdateOptions WithInstall = new() { BuildMerge = true, Install = true };

    /// <summary>Start with Install unticked: nothing is looked up or asked, and there is no yes to an install.</summary>
    [Fact]
    public void AskInstallExtras_InstallUnticked_AsksNothing_AndGivesNoConsent()
    {
        var (start, consent) = GameUpdateCoordinator.AskInstallExtras(
            new GameUpdateOptions { BuildMerge = true, Install = false },
            () => throw new InvalidOperationException("not looked up"),
            (_, _) => throw new InvalidOperationException("not asked"));

        Assert.True(start);
        Assert.Null(consent);
    }

    [Fact]
    public void AskInstallExtras_NothingElseToRemove_StartsWithoutAQuestion()
    {
        var (start, consent) = GameUpdateCoordinator.AskInstallExtras(
            WithInstall, () => new MergeInstallViewModel.InstallExtras([], [], []), (_, _) => throw new InvalidOperationException("not asked"));

        Assert.True(start);
        Assert.False(consent!.RemoveLeftoverArcFission);
        Assert.Empty(consent.OtherFilesAgreed);
    }

    /// <summary>No to removing the other files: nothing starts (the window stays on the ticks) and there is no yes.</summary>
    [Fact]
    public void AskInstallExtras_NoToTheOtherFiles_DoesNotStart_AndGivesNoConsent()
    {
        var asked = new List<string>();
        var (start, consent) = GameUpdateCoordinator.AskInstallExtras(
            WithInstall, () => new MergeInstallViewModel.InstallExtras([], ["A_P.pak", "B_P.pak"], []),
            (message, _) => { asked.Add(message); return false; });

        Assert.False(start);
        Assert.Null(consent);
        Assert.Contains("A_P.pak", Assert.Single(asked));
        Assert.Contains("B_P.pak", asked[0]);
    }

    [Fact]
    public void AskInstallExtras_YesToTheOtherFiles_AgreesToExactlyThose()
    {
        var (start, consent) = GameUpdateCoordinator.AskInstallExtras(
            WithInstall, () => new MergeInstallViewModel.InstallExtras([], ["A_P.pak"], []), (_, _) => true);

        Assert.True(start);
        Assert.False(consent!.RemoveLeftoverArcFission);
        Assert.Equal(["A_P.pak"], consent.OtherFilesAgreed);
    }

    /// <summary>Yes to the leftover ArcFission data adds its files to the ones agreed to; No leaves them out, and was told the older copies still go.</summary>
    [Fact]
    public void AskInstallExtras_LeftoverArcFissionData_YesAddsItsFiles_NoKeepsThemOut()
    {
        var extras = new MergeInstallViewModel.InstallExtras(
            ["ZZ_ArcFission_Data_2_P.pak", "ZZ_ArcFission_Data_3_P.pak"], ["A_P.pak"], ["ZZ_ArcFission_Data_2_P.pak"]);
        var asked = new List<string>();

        var (start, yes) = GameUpdateCoordinator.AskInstallExtras(WithInstall, () => extras, (message, title) => { asked.Add($"{title}: {message}"); return true; });

        Assert.True(start);
        Assert.True(yes!.RemoveLeftoverArcFission);
        Assert.Equal(["A_P.pak", "ZZ_ArcFission_Data_2_P.pak", "ZZ_ArcFission_Data_3_P.pak"], yes.OtherFilesAgreed);
        Assert.Equal(2, asked.Count);
        Assert.StartsWith("Leftover ArcFission data", asked[0]);
        Assert.Contains("No: the newest is kept. Its older copy is still removed, not backed up (ZZ_ArcFission_Data_2_P.pak).", asked[0]);

        var (startKeeping, no) = GameUpdateCoordinator.AskInstallExtras(WithInstall, () => extras, (_, title) => title != "Leftover ArcFission data");

        Assert.True(startKeeping);
        Assert.False(no!.RemoveLeftoverArcFission);
        Assert.Equal(["A_P.pak"], no.OtherFilesAgreed);
    }

    /// <summary>Update everything's build: game data still out of date is refreshed without the question (its Refresh step always runs).</summary>
    [Fact]
    public async Task RebuildWithoutAsking_GameDataOutOfDate_RefreshesAndBuilds_WithNoQuestion()
    {
        var (library, settings) = TwoConflictingMods();
        var rebuild = new FakeRebuildService();
        var freshness = new FakeGameDataFreshness { Outdated = true };
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, settingsService: settings, gameDataFreshness: freshness);
        vm.Queue.Add(library.AllEntries[0]);

        Assert.True(await vm.RebuildWithoutAskingAsync());

        Assert.Equal(1, freshness.RefreshCallCount);
        Assert.Equal(1, rebuild.RebuildCallCount);

        // The Install button's own build asks again afterwards.
        freshness.Outdated = true;
        var asked = false;
        _dialogs.OnConfirm = (_, _) => { asked = true; return false; };
        Assert.False(await vm.RebuildAsync());
        Assert.True(asked);
    }
}
