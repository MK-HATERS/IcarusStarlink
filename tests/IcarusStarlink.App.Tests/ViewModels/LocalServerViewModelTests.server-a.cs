using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Local server run state and automation: Restart, settings changes while running, failed stops, SteamCMD and the daily restart.</summary>
public sealed partial class LocalServerViewModelTests
{
    // ---- Restart checks the form before it stops the server ----

    /// <summary>Regression: Restart stopped the server first, then found Max players = 9 invalid and silently left it stopped.</summary>
    [Fact]
    public async Task Restart_InvalidSettings_LeavesTheServerRunning()
    {
        var (vm, _, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(1234, DateTime.Now);
        vm.Tick();
        vm.MaxPlayersText = "9";

        await vm.RestartCommand.ExecuteAsync(null);

        Assert.Empty(processes.Stopped);
        Assert.Empty(processes.Started);
        Assert.Contains("Max players", vm.ValidationMessage);
        Assert.Contains("wasn't restarted", vm.StatusMessage);
        Assert.Contains("still running", vm.StatusMessage);
    }

    /// <summary>Regression: when the save after the stop failed, the status still said "'X' stopped." and nothing said it wasn't started again.</summary>
    [Fact]
    public async Task Restart_IniQuestionCancelledAfterTheStop_SaysTheServerWasLeftStopped()
    {
        var (vm, processes, _) = BuildWithIni();
        vm.MaxPlayersText = "4";
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();
        processes.DuringStop = () => HandEditIni("MaxPlayers=8\r\nAdminPassword=new\r\n");

        await vm.RestartCommand.ExecuteAsync(null);

        Assert.Equal([9], processes.Stopped);
        Assert.Empty(processes.Started);
        Assert.Contains("wasn't started again", vm.StatusMessage);
        Assert.Contains("click Start", vm.StatusMessage);
    }

    // ---- Saved folder suffix and log file name while the server runs ----

    /// <summary>
    /// Regression: saving a new Saved suffix while the server ran made Starlink lose track of it
    /// (STOPPED, Stop greyed out), and crash restart then started a second copy on the same ports.
    /// </summary>
    [Fact]
    public async Task RunningServer_SavedSuffixChange_IsRefused_AndNoSecondCopyStarts()
    {
        var (vm, store, processes, _) = BuildWithServer();
        processes.RunningFor = p => string.IsNullOrEmpty(p.SavedDirSuffix) ? new RunningServer(9, DateTime.Now) : null;
        await vm.TickAsync();
        await vm.RunAutomationBeatAsync();

        vm.SavedDirSuffix = "_1";
        vm.SaveSettingsCommand.Execute(null);
        await vm.TickAsync();
        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync();

        Assert.Empty(processes.Started);
        Assert.True(vm.IsRunning);
        Assert.Equal("", store.Profiles.Single().SavedDirSuffix);
        Assert.Contains("Stop the server first", vm.ValidationMessage);
    }

    /// <summary>Regression: a new log file name moved the watch to an empty log, so Players read 0 while people played.</summary>
    [Fact]
    public async Task RunningServer_LogFileNameChange_IsRefused_AndPlayersStayKnown()
    {
        var (vm, store, processes, _) = BuildWithServer();
        WriteLog(JoinLine);
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();
        await vm.RunAutomationBeatAsync();
        await vm.LogLoadTask;
        Assert.Single(vm.OnlinePlayers);

        vm.LogFileName = "Friends.log";
        vm.SaveSettingsCommand.Execute(null);
        await vm.LogLoadTask;

        Assert.Single(vm.OnlinePlayers);
        Assert.Equal("", store.Profiles.Single().LogFileName);
        Assert.NotNull(vm.ValidationMessage);
    }

    /// <summary>A blank log file name and the default "Icarus.log" are the same file: that isn't a change.</summary>
    [Fact]
    public async Task RunningServer_DefaultLogFileNameTypedOut_IsNotRefused()
    {
        var (vm, store, processes, _) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();

        vm.LogFileName = DedicatedServerDefaults.DefaultLogFileName;
        vm.SaveSettingsCommand.Execute(null);

        Assert.Null(vm.ValidationMessage);
        Assert.Equal(DedicatedServerDefaults.DefaultLogFileName, store.Profiles.Single().LogFileName);
    }

    /// <summary>Restart is the way to apply a new suffix on a running server: the check must not block it.</summary>
    [Fact]
    public async Task Restart_AppliesANewSavedSuffix()
    {
        var (vm, _, processes, _) = BuildWithServer();
        processes.RunningFor = p => string.IsNullOrEmpty(p.SavedDirSuffix) ? new RunningServer(9, DateTime.Now) : null;
        await vm.TickAsync();
        await vm.RunAutomationBeatAsync();

        vm.SavedDirSuffix = "_1";
        processes.DuringStop = () => processes.RunningFor = null;
        await vm.RestartCommand.ExecuteAsync(null);

        Assert.Equal([9], processes.Stopped);
        Assert.Equal("_1", Assert.Single(processes.Started).SavedDirSuffix);
    }

    /// <summary>Regression: Update saved the new suffix first, then couldn't find the running server and let SteamCMD write under it.</summary>
    [Fact]
    public async Task Update_RunningServerWithANewSavedSuffix_IsRefusedWithAReason()
    {
        WriteManifest(buildId: 1);
        var (vm, _, processes, _) = BuildWithServer();
        processes.RunningFor = p => string.IsNullOrEmpty(p.SavedDirSuffix) ? new RunningServer(9, DateTime.Now) : null;
        await vm.TickAsync();

        vm.SavedDirSuffix = "_1";
        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Empty(SteamCmd.Installs);
        Assert.Empty(processes.Stopped);
        Assert.Contains("Stop the server first", vm.StatusMessage);
    }

    // ---- A stop that fails ----

    /// <summary>
    /// Regression: a stop Windows refused (server started as administrator) threw out of the beat,
    /// so the other servers were skipped, and the update was tried again on every beat.
    /// </summary>
    [Fact]
    public async Task AutoUpdate_StopFails_OtherServersStillLookedAt_AndNoRetryEveryBeat()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, store, processes, _) = BuildWithServer();
        vm.AutoUpdate = true;
        vm.Clock = () => DateTime.Today.AddHours(12);
        var b = new DedicatedServerProfile { Name = "Other", InstallFolder = Path.Combine(_root, "Other") };
        store.Save(b);
        vm.Profiles.Add(b);
        processes.RunningFor = p => new RunningServer(p.Id == b.Id ? 8 : 9, DateTime.Today.AddHours(2));
        await vm.RunAutomationBeatAsync();

        b.DailyBackup = true;
        processes.StopThrows = new System.ComponentModel.Win32Exception(5);
        await vm.RunAutomationBeatAsync();

        var bBackups = ServerBackups.ResolveFolder(Path.Combine(BackupsRoot, "Server"), b);
        Assert.True(Directory.Exists(bBackups) && Directory.GetFiles(bBackups, "*_daily.zip").Length > 0, "the other server's daily backup wasn't made");
        Assert.Contains("left running", vm.StatusMessage);

        await vm.RunAutomationBeatAsync();
        Assert.Equal(1, processes.StopCalls);
        Assert.Empty(processes.Started);
    }

    /// <summary>Regression: a Stop that failed still marked the running server as stopped by the owner, so its real crash later wasn't restarted.</summary>
    [Fact]
    public async Task Stop_Fails_ALaterCrashIsStillRestarted()
    {
        var (vm, _, processes, _) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();
        await vm.RunAutomationBeatAsync();

        processes.StopThrows = new System.ComponentModel.Win32Exception(5);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Contains("Couldn't stop", vm.StatusMessage);

        processes.StopThrows = null;
        await SeeItStopAsync(vm, processes);

        Assert.Single(processes.Started);
    }

    // ---- SteamCMD busy with another server ----

    /// <summary>Regression: while SteamCMD updated server B, automation skipped every server, so A's crash wasn't restarted until it finished.</summary>
    [Fact]
    public async Task CrashRestart_StillRunsWhileAnotherInstallUpdates()
    {
        var (vm, store, processes, _) = BuildWithServer();
        var a = vm.SelectedProfile!;
        var b = new DedicatedServerProfile { Name = "Other", InstallFolder = Path.Combine(_root, "Other") };
        store.Save(b);
        vm.Profiles.Add(b);
        processes.RunningFor = p => p.Id == a.Id ? new RunningServer(9, DateTime.Now) : null;
        await vm.RunAutomationBeatAsync();

        vm.SelectedProfile = b;
        SteamCmd.WaitForCancel = true;
        var update = vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.True(vm.IsSteamCmdBusy);

        processes.RunningFor = _ => null;
        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync();

        Assert.Contains(processes.Started, s => s.Id == a.Id);
        vm.CancelSteamCmdCommand.Execute(null);
        await update;
    }

    /// <summary>The install SteamCMD writes into is still left alone: a server there isn't started mid-write.</summary>
    [Fact]
    public async Task CrashRestart_WaitsWhileSteamCmdWritesItsOwnInstall()
    {
        var (vm, store, processes, _) = BuildWithServer();
        var a = vm.SelectedProfile!;
        var b = new DedicatedServerProfile { Name = "Twin", InstallFolder = _install + Path.DirectorySeparatorChar, SavedDirSuffix = "_2" };
        store.Save(b);
        vm.Profiles.Add(b);
        processes.RunningFor = p => p.Id == a.Id ? new RunningServer(9, DateTime.Now) : null;
        await vm.RunAutomationBeatAsync();

        // A crashes just before the update starts (otherwise Update refuses: a server on the install runs).
        processes.RunningFor = _ => null;
        vm.SelectedProfile = b;
        SteamCmd.WaitForCancel = true;
        var update = vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.True(vm.IsSteamCmdBusy);

        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync();

        Assert.Empty(processes.Started);
        vm.CancelSteamCmdCommand.Execute(null);
        await update;
    }

    // ---- Daily restart ----

    /// <summary>Regression: a server started after the day's restart time was stopped and started again about 5 s after it showed RUNNING.</summary>
    [Fact]
    public async Task DailyRestart_ServerStartedAfterTheSlot_IsLeftAlone()
    {
        var (vm, _, processes, _) = BuildWithServer();
        vm.DailyRestartEnabled = true;
        vm.DailyRestartTime = "05:00";
        vm.Clock = () => DateTime.Today.AddHours(5).AddMinutes(20);
        processes.Running = new RunningServer(9, DateTime.Today.AddHours(5).AddMinutes(19));

        await vm.RunAutomationBeatAsync();

        Assert.Empty(processes.Stopped);
    }

    /// <summary>Regression: an update the hourly check found while players were on wasn't installed by the daily restart, so the server was restarted twice in a row.</summary>
    [Fact]
    public async Task DailyRestart_InstallsAnUpdateTheHourlyCheckFound()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        vm.AutoUpdate = true;
        vm.DailyRestartEnabled = true;
        vm.DailyRestartTime = "05:00";
        var now = DateTime.Today.AddHours(4).AddMinutes(59).AddSeconds(58);
        vm.Clock = () => now;
        processes.Running = new RunningServer(9, DateTime.Today.AddHours(2));
        await vm.RunAutomationBeatAsync();

        now = DateTime.Today.AddHours(5).AddSeconds(3);
        await vm.RunAutomationBeatAsync();
        Assert.Single(SteamCmd.Installs);

        await vm.RunAutomationBeatAsync();
        Assert.Single(processes.Stopped);
    }

    // ---- Buttons that follow the selection and automation ----

    /// <summary>Regression: with no server at first, "Open these ports" and "Remove Starlink's rules" stayed greyed out after one was added.</summary>
    [Fact]
    public void AddingTheFirstServer_EnablesTheFirewallButtons()
    {
        var (vm, _, _, _) = Build(pick: _install);
        var openSeen = new List<bool>();
        vm.OpenFirewallPortsCommand.CanExecuteChanged += (_, _) => openSeen.Add(vm.OpenFirewallPortsCommand.CanExecute(null));
        var removeSeen = new List<bool>();
        vm.RemoveFirewallRulesCommand.CanExecuteChanged += (_, _) => removeSeen.Add(vm.RemoveFirewallRulesCommand.CanExecute(null));

        vm.AddExistingServerCommand.Execute(null);

        Assert.True(openSeen.LastOrDefault(false), "Open these ports wasn't told it can run once a server was added");
        Assert.True(removeSeen.LastOrDefault(false), "Remove Starlink's rules wasn't told it can run once a server was added");
    }

    /// <summary>Regression: after a daily restart with an update, Check for update stayed greyed out while Update came back.</summary>
    [Fact]
    public async Task DailyRestartWithUpdate_LeavesCheckForUpdateEnabled()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        var seen = new List<bool>();
        vm.CheckForUpdateCommand.CanExecuteChanged += (_, _) => seen.Add(vm.CheckForUpdateCommand.CanExecute(null));
        vm.DailyRestartEnabled = true;
        vm.DailyRestartTime = "05:00";
        vm.UpdateOnRestart = true;
        vm.Clock = () => DateTime.Today.AddHours(5).AddSeconds(10);
        processes.Running = new RunningServer(9, DateTime.Today.AddHours(2));

        await vm.RunAutomationBeatAsync();

        Assert.Single(SteamCmd.Installs);
        Assert.True(seen.LastOrDefault(false), "Check for update wasn't told it can run again after the restart");
    }

    /// <summary>A backup greys Check for update (as it does Update) and brings it back when done.</summary>
    [Fact]
    public async Task BackUpNow_GreysCheckForUpdate_ThenBringsItBack()
    {
        var (vm, _, _, _) = BuildWithServer();
        var seen = new List<bool>();
        vm.CheckForUpdateCommand.CanExecuteChanged += (_, _) => seen.Add(vm.CheckForUpdateCommand.CanExecute(null));

        await vm.BackUpNowCommand.ExecuteAsync(null);

        Assert.Contains(false, seen);
        Assert.True(seen.LastOrDefault(false));
    }

    // ---- The server list is locked while a server starts, stops or updates ----

    /// <summary>Regression: Add existing server… stayed clickable during a Start; it changed the selection, and the start then reported "didn't appear" for a server that did.</summary>
    [Fact]
    public async Task AddExistingServer_IsGreyedWhileAServerStarts()
    {
        var (vm, _, processes, _) = BuildWithServer();
        var seen = new List<bool>();
        vm.AddExistingServerCommand.CanExecuteChanged += (_, _) => seen.Add(vm.AddExistingServerCommand.CanExecute(null));
        bool? canAdd = null;
        processes.DuringStart = () => canAdd = vm.AddExistingServerCommand.CanExecute(null);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(canAdd);
        Assert.Contains(false, seen);
        Assert.True(seen.LastOrDefault(false), "Add existing server… wasn't told it can run again after the start");
    }

    /// <summary>Regression: Remove stayed clickable while automation restarted the server; the restart carried on and launched a server nothing watched any more.</summary>
    [Fact]
    public async Task Remove_IsGreyedWhileAutomationRestartsTheServer()
    {
        var (vm, _, processes, _) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.RunAutomationBeatAsync();
        bool? canRemove = null;
        processes.DuringStart = () => canRemove = vm.RemoveProfileCommand.CanExecute(null);

        processes.Running = null;
        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync();

        Assert.NotNull(canRemove);
        Assert.False(canRemove);
        Assert.True(vm.RemoveProfileCommand.CanExecute(null));
    }

    // ---- Check for update and Update ----

    /// <summary>Regression: after an update the box showed a full 100 % bar while a later check was still asking Steam.</summary>
    [Fact]
    public async Task CheckForUpdate_AfterAnUpdate_ShowsAMovingBar()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        await vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.Equal(100, vm.SteamCmdProgress);

        bool? unknown = null;
        SteamCmd.CheckHook = _ =>
        {
            unknown = vm.SteamCmdProgressUnknown;
            return Task.FromResult<int?>(2);
        };
        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.True(unknown);
    }

    /// <summary>The hourly check's own run resets the bar the same way.</summary>
    [Fact]
    public async Task HourlyCheck_AfterAnUpdate_ShowsAMovingBar()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        await vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.Equal(100, vm.SteamCmdProgress);
        vm.AutoUpdate = true;
        vm.Clock = () => DateTime.Today.AddHours(12);
        processes.Running = new RunningServer(9, DateTime.Today.AddHours(2));
        bool? unknown = null;
        SteamCmd.CheckHook = _ =>
        {
            unknown = vm.SteamCmdProgressUnknown;
            return Task.FromResult<int?>(2);
        };

        await vm.RunAutomationBeatAsync();

        Assert.True(unknown);
    }

    /// <summary>Regression: when the hourly check found an update for a server that wasn't selected, selecting it showed "Build 1 · public" with no word of the update.</summary>
    [Fact]
    public async Task AutomationFoundAnUpdate_ForAnotherServer_ShowsWhenItIsSelected()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, store, processes, _) = BuildWithServer();
        var a = vm.SelectedProfile!;
        vm.AutoUpdate = true;
        vm.Clock = () => DateTime.Today.AddHours(12);
        var b = new DedicatedServerProfile { Name = "Other", InstallFolder = Path.Combine(_root, "Other") };
        store.Save(b);
        vm.Profiles.Add(b);
        vm.SelectedProfile = b;
        processes.RunningFor = p => p.Id == a.Id ? new RunningServer(9, DateTime.Today.AddHours(2)) : null;

        await vm.RunAutomationBeatAsync();
        Assert.Equal(1, SteamCmd.Checks);
        vm.SelectedProfile = vm.Profiles.First(p => p.Id == a.Id);

        Assert.Equal(2, vm.LatestBuildId);
        Assert.Contains("update available (build 2)", vm.BuildSummary);
    }

    /// <summary>Regression: Update on a running server stopped it and left it stopped, with nothing saying so.</summary>
    [Fact]
    public async Task Update_ARunningServer_StartsItAgainAfterwards()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, dialogs) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Contains("start it again", Assert.Single(dialogs.Messages));
        Assert.Equal([9], processes.Stopped);
        Assert.Single(SteamCmd.Installs);
        Assert.Single(processes.Started);
        Assert.NotNull(vm.Running);
        Assert.Contains("Updated", vm.StatusMessage);
        Assert.Contains("is running", vm.StatusMessage);
    }

    /// <summary>A failed update still starts the server again, on the build it already had (as automation does).</summary>
    [Fact]
    public async Task Update_ARunningServer_FailedUpdate_StartsTheBuildItHad()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        SteamCmd.FailWith = "Error! App '2089300' state is 0x202 after update job.";
        var (vm, _, processes, _) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Single(processes.Started);
        Assert.Contains("Update failed", vm.StatusMessage);
        Assert.Contains("is running", vm.StatusMessage);
    }

    /// <summary>When it can't be started again (another server was selected by the end), the result says it was left stopped.</summary>
    [Fact]
    public async Task Update_ARunningServer_NotStartedAgain_SaysItWasLeftStopped()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, store, processes, _) = BuildWithServer();
        var other = new DedicatedServerProfile { Name = "Other", InstallFolder = Path.Combine(_root, "Other") };
        store.Save(other);
        vm.Profiles.Add(other);
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();
        SteamCmd.DuringInstall = () => vm.SelectedProfile = other;

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Empty(processes.Started);
        Assert.Contains("stopped for the update", vm.StatusMessage);
    }

    /// <summary>A stopped server stays stopped after Update.</summary>
    [Fact]
    public async Task Update_AStoppedServer_DoesNotStartIt()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Single(SteamCmd.Installs);
        Assert.Empty(processes.Started);
        Assert.DoesNotContain("stopped for the update", vm.StatusMessage);
    }

    // ---- SteamCMD's "validate": repairs only, not every routine update ----

    /// <summary>Regression: every Update re-checked the whole ~10 GB install, keeping the server down far longer than the download needs.</summary>
    [Fact]
    public async Task Update_OnTheSameBranch_SkipsValidate()
    {
        WriteManifest(buildId: 1);
        var (vm, _, _, _) = BuildWithServer();

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Equal([false], SteamCmd.Validates);
    }

    /// <summary>Regression: the automatic update (daily restart with update) validated too.</summary>
    [Fact]
    public async Task AutomaticUpdate_SkipsValidate()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        vm.DailyRestartEnabled = true;
        vm.DailyRestartTime = "05:00";
        vm.UpdateOnRestart = true;
        vm.Clock = () => DateTime.Today.AddHours(5).AddSeconds(10);
        processes.Running = new RunningServer(9, DateTime.Today.AddHours(2));

        await vm.RunAutomationBeatAsync();

        Assert.Equal([false], SteamCmd.Validates);
    }

    /// <summary>A branch switch replaces many files: SteamCMD checks them all.</summary>
    [Fact]
    public async Task Update_ThatSwitchesBranch_Validates()
    {
        WriteManifest(buildId: 1);
        var (vm, _, _, _) = BuildWithServer();
        vm.Branch = "experimental";

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Equal([(_install, "experimental")], SteamCmd.Installs);
        Assert.Equal([true], SteamCmd.Validates);
    }

    /// <summary>A server SteamCMD left no record in can't be told apart from a damaged one: it is validated.</summary>
    [Fact]
    public async Task Update_WithNoSteamRecord_Validates()
    {
        var (vm, _, _, _) = BuildWithServer();

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Equal([true], SteamCmd.Validates);
    }

    /// <summary>Regression: a retry after a failed update didn't validate either, so files the failed run left half-written stayed that way.</summary>
    [Fact]
    public async Task Update_AfterAFailedUpdate_ValidatesTheRetry()
    {
        WriteManifest(buildId: 1);
        var (vm, _, _, _) = BuildWithServer();
        SteamCmd.FailWith = "ERROR! Failed to install app '2089300' (Disk write failure)";
        await vm.UpdateServerCommand.ExecuteAsync(null);
        SteamCmd.FailWith = null;

        await vm.UpdateServerCommand.ExecuteAsync(null);
        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Equal([false, true, false], SteamCmd.Validates);
    }

    [Fact]
    public async Task InstallNewServer_Validates()
    {
        SteamCmd.CreateServerOnInstall = true;
        var (vm, _, _, _) = Build(pick: Path.Combine(_root, "NewServer"));

        await vm.InstallNewServerCommand.ExecuteAsync(null);

        Assert.Equal([true], SteamCmd.Validates);
    }

    /// <summary>Verify files runs SteamCMD's validate on the same branch; a running server is stopped for it and started again.</summary>
    [Fact]
    public async Task VerifyFiles_ValidatesAndStartsARunningServerAgain()
    {
        WriteManifest(buildId: 1);
        var (vm, _, processes, dialogs) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();

        await vm.VerifyServerFilesCommand.ExecuteAsync(null);

        Assert.Contains(dialogs.Messages, m => m.Contains("check its files", StringComparison.Ordinal));
        Assert.Equal([(_install, "public")], SteamCmd.Installs);
        Assert.Equal([true], SteamCmd.Validates);
        Assert.Equal([9], processes.Stopped);
        Assert.Single(processes.Started);
    }
}
