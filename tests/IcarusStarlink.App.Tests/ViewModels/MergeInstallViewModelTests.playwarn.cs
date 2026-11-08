using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>"Install when Icarus closes": offered when Install is refused because the game runs, and never a copy without the install's own question.</summary>
public sealed partial class MergeInstallViewModelTests
{
    /// <summary>A staged build Copy to game offers (the start-up check awaited, so it can't land partway through a test).</summary>
    private async Task<(MergeInstallViewModel Vm, FakeInstallService Install, FakeGameProcessChecker Game)> WhenGameClosesSetup()
    {
        var settings = new FakeSettingsService();
        var (contentPath, _) = MakeGameContent();
        settings.Current.IcarusContentPath = contentPath;
        var install = new FakeInstallService();
        var game = new FakeGameProcessChecker(running: true);
        var vm = CreateViewModel(installService: install, settingsService: settings, game: game);
        await vm.RefreshHasExistingInstallAsync();
        Assert.True(vm.CanCopyToGame);
        return (vm, install, game);
    }

    /// <summary>Refused because Icarus runs: the player can ask to install when it closes; then it asks its usual question, checks the game again and installs.</summary>
    [Fact]
    public async Task InstallWhenGameCloses_AfterARefusal_AsksAndInstallsOnceTheGameCloses()
    {
        var (vm, install, game) = await WhenGameClosesSetup();
        var questions = 0;
        _dialogs.OnConfirm = (_, title) => { questions++; return title == "Install to Icarus"; };

        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, await vm.InstallAsync());
        Assert.True(vm.CanInstallWhenGameCloses);

        vm.InstallWhenGameClosesCommand.Execute(null);
        Assert.True(vm.IsWaitingForGameToClose);
        Assert.False(vm.CanInstallWhenGameCloses);
        Assert.Contains("when Icarus closes", vm.InstallStatusMessage);

        game.Running = false;
        await vm.OnGameClosedAsync();

        Assert.Equal(1, questions);
        Assert.Equal(1, install.InstallCallCount);
        Assert.False(vm.IsWaitingForGameToClose);
    }

    /// <summary>Not asked for: the game closing installs nothing.</summary>
    [Fact]
    public async Task GameCloses_NothingWaiting_InstallsNothing()
    {
        var (vm, install, game) = await WhenGameClosesSetup();
        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, await vm.InstallAsync());

        game.Running = false;
        await vm.OnGameClosedAsync();

        Assert.Equal(0, install.InstallCallCount);
        Assert.True(vm.CanInstallWhenGameCloses); // still offered; nothing done on its own
    }

    /// <summary>The staged pak stopped being installable meanwhile (a build failed or was cancelled): the wait is dropped, and the game closing asks nothing.</summary>
    [Fact]
    public async Task InstallWhenGameCloses_TheStagedPakChangedMeanwhile_IsDropped()
    {
        var (vm, install, game) = await WhenGameClosesSetup();
        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, await vm.InstallAsync());
        vm.InstallWhenGameClosesCommand.Execute(null);

        vm.CanCopyToGame = false;
        Assert.False(vm.IsWaitingForGameToClose);

        game.Running = false;
        await vm.OnGameClosedAsync();

        Assert.Equal(0, install.InstallCallCount);
    }

    /// <summary>"Don't wait" takes the request back.</summary>
    [Fact]
    public async Task StopWaitingForGameToClose_TakesTheRequestBack()
    {
        var (vm, install, game) = await WhenGameClosesSetup();
        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, await vm.InstallAsync());
        vm.InstallWhenGameClosesCommand.Execute(null);

        vm.StopWaitingForGameToCloseCommand.Execute(null);

        Assert.False(vm.IsWaitingForGameToClose);
        Assert.True(vm.CanInstallWhenGameCloses);
        game.Running = false;
        await vm.OnGameClosedAsync();
        Assert.Equal(0, install.InstallCallCount);
    }

    /// <summary>The header's game watch reaches Merge &amp; Install through GameClosedMessage.</summary>
    [Fact]
    public async Task GameClosedMessage_StartsTheWaitingInstall()
    {
        var (vm, _, game) = await WhenGameClosesSetup();
        _dialogs.OnConfirm = (_, _) => false;
        Assert.Equal(MergeInstallViewModel.InstallOutcome.NotDone, await vm.InstallAsync());
        vm.InstallWhenGameClosesCommand.Execute(null);

        game.Running = false;
        _messenger.Send(new GameClosedMessage());

        Assert.False(vm.IsWaitingForGameToClose);
    }
}
