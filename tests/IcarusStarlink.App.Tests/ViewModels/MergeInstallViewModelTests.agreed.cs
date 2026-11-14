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
