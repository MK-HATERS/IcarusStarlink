using System.IO;
using System.Net.Http;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// "The server tab needs some love": the SteamCMD card under the status card, the Connect card
/// (the address to share for Direct Connect), and the Overview's Now / Coming up / Recent events.
/// The network and the clipboard are fakes: nothing goes online and the real clipboard is untouched.
/// </summary>
public sealed partial class LocalServerViewModelTests
{
    private FakeNetwork Network { get; } = new();

    /// <summary>What Copy buttons put on the (fake) clipboard.</summary>
    private List<string> Copied { get; } = [];

    private const string ProspectLine = "[2026.08.12-17.32.01:410][337]LogLocalSessionSubsystem: Display: SetActiveProspectInfo - ProspectID: Hard Elysium JULY | ProspectDTKey: OpenWorld_Elysium";

    private const string CleanExitLine = "[2026.08.12-18.00.00:000][  1]LogExit: Exiting.";

    // ---- 1. The SteamCMD card ----

    /// <summary>A first install has no server selected yet: the card still shows, titled with what it does and the phase.</summary>
    [Fact]
    public async Task SteamCmdCard_InstallingANewServer_ShowsWithNoServerSelected_WithThePhaseInItsTitle()
    {
        SteamCmd.CreateServerOnInstall = true;
        SteamCmd.WaitForCancel = true;
        var (vm, _, _, _) = Build(pick: Path.Combine(_root, "NewServer"));
        Assert.False(vm.ShowSteamCmdPanel);

        var install = vm.InstallNewServerCommand.ExecuteAsync(null);

        Assert.True(vm.HasNoProfile);
        Assert.True(vm.ShowSteamCmdPanel);
        Assert.Equal("Installing a new server · downloading 50%", vm.SteamCmdTitle);
        Assert.Equal(50, vm.SteamCmdProgress);
        Assert.False(vm.SteamCmdProgressUnknown);
        Assert.True(vm.IsSteamCmdBusy);
        Assert.True(vm.CancelSteamCmdCommand.CanExecute(null));

        vm.CancelSteamCmdCommand.Execute(null);
        await install;

        Assert.True(vm.ShowSteamCmdPanel, "the result stays until Hide");
        Assert.True(vm.IsSteamCmdIdle);
        Assert.Equal("Installing a new server · cancelled", vm.SteamCmdTitle);
        vm.DismissSteamCmdOutputCommand.Execute(null);
        Assert.False(vm.ShowSteamCmdPanel);
    }

    [Fact]
    public async Task SteamCmdCard_UpdatingAServer_NamesIt_AndTheNowLineFollowsTheProgress()
    {
        WriteManifest(buildId: 1);
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        string? titleBefore = null;
        string? nowBefore = null;
        bool? unknownBefore = null;
        SteamCmd.DuringInstall = () =>
        {
            titleBefore = vm.SteamCmdTitle;
            nowBefore = vm.NowLine;
            unknownBefore = vm.SteamCmdProgressUnknown;
        };
        SteamCmd.WaitForCancel = true;

        var update = vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Equal("Updating 'Icarus Dedicated Server' · starting SteamCMD…", titleBefore);
        Assert.Equal("Updating with SteamCMD (starting SteamCMD…)", nowBefore);
        Assert.True(unknownBefore, "no percentage yet: the bar moves without a value");
        Assert.Equal("Updating 'Icarus Dedicated Server' · downloading 50%", vm.SteamCmdTitle);
        Assert.Equal("Updating with SteamCMD (downloading 50%)", vm.NowLine);

        vm.CancelSteamCmdCommand.Execute(null);
        await update;
        Assert.Equal("Updating 'Icarus Dedicated Server' · cancelled", vm.SteamCmdTitle);
    }

    [Fact]
    public async Task SteamCmdCard_AfterAnUpdate_SaysFinishedOrFailed()
    {
        WriteManifest(buildId: 1);
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        await vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.Equal("Updating 'Icarus Dedicated Server' · finished", vm.SteamCmdTitle);
        Assert.Equal(100, vm.SteamCmdProgress);
        Assert.Contains(vm.RecentEvents, e => e.EndsWith("Update installed"));

        SteamCmd.FailWith = "ERROR! Failed to install app '2089300' (Disk write failure)";
        await vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.Equal("Updating 'Icarus Dedicated Server' · failed", vm.SteamCmdTitle);
        Assert.True(vm.ShowSteamCmdPanel);
    }

    // ---- 2. Connect ----

    [Fact]
    public async Task Connect_LanAddress_SkipsVirtualAdapters_AndCopiesIpColonGamePort()
    {
        Network.Adapters =
        [
            new NetworkAdapterInfo("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", true, NetworkAdapterKind.Ethernet, true, ["172.24.80.1"]),
            new NetworkAdapterInfo("Ethernet", "Intel(R) Ethernet Connection", true, NetworkAdapterKind.Ethernet, true, ["192.168.1.20"]),
        ];
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        Assert.False(vm.CopyLanAddressCommand.CanExecute(null));

        await vm.RefreshLanAddressCommand.ExecuteAsync(null);

        Assert.Equal("192.168.1.20:17777", vm.LanJoinAddress);
        Assert.Equal("192.168.1.20:17777", vm.LanAddressText);
        vm.CopyLanAddressCommand.Execute(null);
        Assert.Equal(["192.168.1.20:17777"], Copied);
        Assert.Contains("Copied 192.168.1.20:17777", vm.StatusMessage);
        Assert.Contains("UDP 17777 and UDP 27015 forwarded on your router to this PC (192.168.1.20)", vm.PortForwardHint);
    }

    /// <summary>The address follows the server's saved game port (Settings › Branch &amp; ports).</summary>
    [Fact]
    public async Task Connect_UsesTheSavedGamePort()
    {
        Network.Adapters = [new NetworkAdapterInfo("Wi-Fi", "Wi-Fi", true, NetworkAdapterKind.Wireless, true, ["192.168.0.7"])];
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        await vm.RefreshLanAddressCommand.ExecuteAsync(null);

        vm.PortText = "17780";
        vm.QueryPortText = "27020";
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal("192.168.0.7:17780", vm.LanJoinAddress);
        Assert.Contains("UDP 17780 and UDP 27020", vm.PortForwardHint);
    }

    [Fact]
    public async Task Connect_NoAdapterQualifies_SaysSoInPlainWords()
    {
        Network.Adapters = [new NetworkAdapterInfo("Ethernet", "Intel", true, NetworkAdapterKind.Ethernet, HasGateway: false, ["192.168.1.20"])];
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        await vm.RefreshLanAddressCommand.ExecuteAsync(null);

        Assert.Null(vm.LanJoinAddress);
        Assert.StartsWith("No home network address found", vm.LanAddressText);
        Assert.False(vm.CopyLanAddressCommand.CanExecute(null));
    }

    /// <summary>The public IP service is asked only on the button: not on load, select, ticks or beats.</summary>
    [Fact]
    public async Task Connect_PublicIp_IsOnlyAskedForOnTheButton_ThenCopied()
    {
        Network.PublicReply = _ => Task.FromResult("203.0.113.7\n");
        var (vm, _, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        await vm.RefreshLanAddressCommand.ExecuteAsync(null);
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();
        await vm.RunAutomationBeatAsync();
        Assert.Equal(0, Network.PublicCalls);
        Assert.Null(vm.PublicJoinAddress);
        Assert.False(vm.CopyPublicAddressCommand.CanExecute(null));

        await vm.ShowPublicAddressCommand.ExecuteAsync(null);

        Assert.Equal(1, Network.PublicCalls);
        Assert.Equal("203.0.113.7:17777", vm.PublicJoinAddress);
        Assert.Null(vm.PublicAddressProblem);
        vm.CopyPublicAddressCommand.Execute(null);
        Assert.Equal(["203.0.113.7:17777"], Copied);
    }

    [Fact]
    public async Task Connect_PublicIp_ReplyThatIsntAnAddress_IsRefused()
    {
        Network.PublicReply = _ => Task.FromResult("<html>Too many requests</html>");
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        await vm.ShowPublicAddressCommand.ExecuteAsync(null);

        Assert.Null(vm.PublicJoinAddress);
        Assert.Contains("isn't an IP address", vm.PublicAddressProblem);
        Assert.True(vm.ShowPublicAddressCommand.CanExecute(null), "it can be asked again");
    }

    [Fact]
    public async Task Connect_PublicIp_NoConnection_SaysSo()
    {
        Network.PublicReply = _ => throw new HttpRequestException("No such host is known.");
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        await vm.ShowPublicAddressCommand.ExecuteAsync(null);

        Assert.Null(vm.PublicJoinAddress);
        Assert.Equal("Couldn't reach the address service (https://api.ipify.org). Check your internet connection and try again.", vm.PublicAddressProblem);
    }

    [Fact]
    public async Task Connect_PublicIp_NoAnswerInTime_SaysSo()
    {
        Network.PublicReply = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "";
        };
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.PublicIpTimeout = TimeSpan.FromMilliseconds(50);

        await vm.ShowPublicAddressCommand.ExecuteAsync(null);

        Assert.Contains("didn't answer within", vm.PublicAddressProblem);
        Assert.False(vm.IsLookingUpPublicAddress);
    }

    // ---- 3. Now ----

    [Fact]
    public void Now_StoppedWithNoHistory_SaysStopped()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        Assert.Equal("Stopped", vm.NowLine);
    }

    [Fact]
    public async Task Now_Running_NamesTheWorldPlayersAndUptime()
    {
        var (vm, _, processes, _) = BuildWithServer();
        WriteLog(JoinLine, ProspectLine);
        processes.Running = new RunningServer(9, DateTime.Now.AddHours(-3).AddMinutes(-12));

        await vm.TickAsync();

        Assert.Equal("Running · world 'Hard Elysium JULY' · 1 player · up 3h 12m", vm.NowLine);
    }

    /// <summary>Just started, with a world to resume, and no prospect in its log yet: still loading.</summary>
    [Fact]
    public async Task Now_JustStartedWithAWorldToLoad_SaysLoadingTheWorld_ThenTheLobbyWithout()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nResumeProspect=True\r\nLastProspectName=Olympus_Base\r\n");
        var (vm, _, processes, _) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now.AddSeconds(-30));
        await vm.TickAsync();
        Assert.Equal("Starting… (loading the world)", vm.NowLine);

        processes.Running = new RunningServer(10, DateTime.Now.AddMinutes(-10));
        await vm.TickAsync();
        Assert.StartsWith("Running · in the lobby · nobody online · up 10m", vm.NowLine);
    }

    [Fact]
    public async Task Now_WhileStarting_SaysItIsWaitingForTheServer()
    {
        var (vm, _, processes, _) = BuildWithServer();
        processes.RunAfterStart = false;
        string? during = null;
        processes.DuringStart = () => during = vm.NowLine;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Starting… (waiting for the server to open)", during);
    }

    [Fact]
    public async Task Now_StoppedCleanly_SaysWhen_AndRecentEventsHaveIt()
    {
        var (vm, _, processes, _) = BuildWithServer();
        var now = DateTime.Now;
        vm.Clock = () => now;
        processes.Running = new RunningServer(9, now.AddHours(-1));
        await vm.RunAutomationBeatAsync();
        AppendLogOrWrite(CleanExitLine);

        await SeeItStopAsync(vm, processes);
        await vm.TickAsync();

        Assert.Equal($"Stopped · last stopped cleanly at {now:HH:mm}", vm.NowLine);
        Assert.EndsWith("Server stopped cleanly", vm.RecentEvents[0]);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task Now_StoppedByTheOwner_SaysSo()
    {
        var (vm, _, processes, _) = BuildWithServer();
        var now = DateTime.Now;
        vm.Clock = () => now;
        processes.Running = new RunningServer(9, now.AddHours(-1));
        await vm.RunAutomationBeatAsync();
        await vm.TickAsync();

        await vm.StopCommand.ExecuteAsync(null);
        await SeeItStopAsync(vm, processes);

        Assert.Equal($"Stopped · you stopped it at {now:HH:mm}", vm.NowLine);
        Assert.EndsWith("Server stopped by you", vm.RecentEvents[0]);
    }

    /// <summary>A crash restarted by automation: "Automation: restarting it after a crash…" while it works, then the running line says when it crashed.</summary>
    [Fact]
    public async Task Now_CrashRestarted_SaysSoWhileRestarting_AndAfterwards()
    {
        var (vm, _, processes, _) = BuildWithServer();
        var now = DateTime.Now;
        vm.Clock = () => now;
        processes.Running = new RunningServer(9, now.AddHours(-1));
        await vm.RunAutomationBeatAsync();
        string? during = null;
        processes.DuringStart = () => during = vm.NowLine;

        await SeeItStopAsync(vm, processes);
        await vm.TickAsync();

        Assert.Equal("Automation: restarting it after a crash…", during);
        Assert.Single(processes.Started);
        Assert.EndsWith($"restarted after a crash at {now:HH:mm}", vm.NowLine);
        Assert.StartsWith("Running", vm.NowLine);
        Assert.Contains(vm.RecentEvents, e => e.EndsWith("Server crashed (it stopped without a clean exit in its log)"));
        Assert.Contains(vm.NextUp, l => l.Contains("1 of 3 restarts used in the last 10 minutes"));
    }

    [Fact]
    public async Task Now_CrashWithCrashRestartOff_SaysItWasLeftStopped()
    {
        var (vm, _, processes, _) = BuildWithServer();
        vm.AutoRestartOnCrash = false;
        var now = DateTime.Now;
        vm.Clock = () => now;
        processes.Running = new RunningServer(9, now.AddHours(-1));
        await vm.RunAutomationBeatAsync();

        await SeeItStopAsync(vm, processes);
        await vm.TickAsync();

        Assert.Equal($"Stopped · it crashed at {now:HH:mm} ('Restart it if it crashes' is off)", vm.NowLine);
        Assert.Empty(processes.Started);
    }

    // ---- 3. Coming up ----

    [Fact]
    public void ComingUp_ShowsALineForEachOptionThatIsOn()
    {
        var (vm, _, _, _) = BuildWithServer();
        vm.Clock = () => DateTime.Today.AddHours(4);
        vm.AutoRestartOnCrash = false;
        Assert.Equal(["No automation is on for this server."], vm.NextUp);

        vm.DailyRestartEnabled = true;
        vm.DailyRestartTime = "05:00";
        vm.AutoUpdate = true;
        vm.DailyBackup = true;

        Assert.Equal(
        [
            "Next daily restart today at 05:00.",
            "Checks for an update as soon as it runs.",
            "Backs up its worlds as soon as it runs (daily backup).",
        ], vm.NextUp);
    }

    // ---- 3. Recent events ----

    /// <summary>Newest first, at most eight, without the start-up's "Mod pak loaded" lines; the Log tab keeps them all.</summary>
    [Fact]
    public async Task RecentEvents_AreTheNewestEight_NewestFirst_WithoutModPakLines()
    {
        var (vm, _, processes, _) = BuildWithServer();
        var lines = new List<string>
        {
            "[2026.08.12-17.00.00:000][  1]LogPakFile: Display: Mounting pak file ../../../Icarus/Content/Paks/mods/ZZ_Mod_P.pak",
        };
        for (var i = 1; i <= 10; i++)
        {
            lines.Add($"[2026.08.12-17.{i:00}.00:000][  1]LogNet: Join succeeded: Player{i}");
        }

        lines.Add("[2026.08.12-17.30.00:000][  1]LogPakFile: Display: Mounting pak file ../../../Icarus/Content/Paks/mods/ZZ_Other_P.pak");
        WriteLog([.. lines]);
        processes.Running = new RunningServer(9, DateTime.Now);

        await vm.TickAsync();

        Assert.Equal(LocalServerViewModel.MaxRecentEvents, vm.RecentEvents.Count);
        Assert.EndsWith("Player10 joined", vm.RecentEvents[0]);
        Assert.EndsWith("Player3 joined", vm.RecentEvents[^1]);
        Assert.DoesNotContain(vm.RecentEvents, e => e.Contains("Mod pak loaded"));
        Assert.Contains(vm.Events, e => e.Contains("Mod pak loaded: ZZ_Other_P.pak"));
        Assert.True(vm.HasRecentEvents);
    }

    [Fact]
    public async Task RecentEvents_AServerSeenStarting_IsAnEvent()
    {
        var (vm, _, processes, _) = BuildWithServer();
        await vm.RunAutomationBeatAsync(); // seen stopped
        processes.Running = new RunningServer(9, DateTime.Now);

        await vm.RunAutomationBeatAsync();

        Assert.EndsWith("Server started", vm.RecentEvents[0]);
    }

    /// <summary>A server already running when Starlink opens wasn't "started" just now.</summary>
    [Fact]
    public async Task RecentEvents_AServerAlreadyRunningAtTheFirstLook_IsNotAStart()
    {
        var (vm, _, processes, _) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now.AddHours(-2));

        await vm.RunAutomationBeatAsync();

        Assert.DoesNotContain(vm.RecentEvents, e => e.EndsWith("Server started"));
    }

    [Fact]
    public async Task RecentEvents_ABackupMade_IsAnEvent()
    {
        var (vm, _, _, _) = BuildWithServer();
        Directory.CreateDirectory(DedicatedServerLayout.SavedFolder(_install, null));

        await vm.BackUpNowCommand.ExecuteAsync(null);

        Assert.Contains(vm.RecentEvents, e => e.Contains("Backup made: "));
    }

    // ---- Review 2026-10-30: Check for update and the hourly auto-update ----

    /// <summary>
    /// Check for update doesn't lock the server list. A check on a public server that finished
    /// after the owner picked an experimental one put the public build on it: "update available
    /// (build …)" for a build of the other branch.
    /// </summary>
    [Fact]
    public async Task CheckForUpdate_ThatFinishesAfterAnotherServerIsPicked_LeavesThatServerAlone()
    {
        WriteManifest(buildId: 1);
        var other = Path.Combine(_root, "Experimental Server");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(other).FullName, "IcarusServer.exe"), "");
        var manifest = DedicatedServerLayout.AppManifestPath(other);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"buildid\"\t\t\"7\"\n\t\"UserConfig\"\n\t{\n\t\t\"BetaKey\"\t\t\"experimental\"\n\t}\n}\n");
        var store = new FakeStore();
        store.Profiles.Add(new DedicatedServerProfile { Name = "Public", InstallFolder = _install });
        store.Profiles.Add(new DedicatedServerProfile { Name = "Experimental", InstallFolder = other, Branch = "experimental" });
        var answer = new TaskCompletionSource<int?>();
        SteamCmd.CheckHook = _ => answer.Task;
        var (vm, _, _, _) = Build(existingStore: store);
        Assert.Equal("Public", vm.SelectedProfile?.Name);

        var check = vm.CheckForUpdateCommand.ExecuteAsync(null);
        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "Experimental");
        answer.SetResult(2);
        await check;

        Assert.Null(vm.LatestBuildId);
        Assert.Equal("Build 7 · experimental", vm.BuildSummary);
    }

    /// <summary>
    /// The hourly check found a newer build while players were on; the owner then pressed Update
    /// and started the server again. Automation still had "a newer build is out" and, once the
    /// server was empty, stopped it and ran SteamCMD again for the build it already had.
    /// </summary>
    [Fact]
    public async Task AutoUpdate_AfterTheOwnerUpdatedByHand_DoesNotStopTheServerToUpdateAgain()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        vm.AutoUpdate = true;
        var now = DateTime.Today.AddHours(12);
        vm.Clock = () => now;
        WriteLog(JoinLine);
        processes.Running = new RunningServer(9, DateTime.Now);
        vm.Tick();
        await vm.RunAutomationBeatAsync(); // the hourly check: newer build, but someone is on
        Assert.Contains(vm.NextUp, l => l.Contains("installs as soon as nobody is online"));

        await vm.UpdateServerCommand.ExecuteAsync(null); // the owner updates by hand (stops 9)
        await SeeItStopAsync(vm, processes);
        Assert.Empty(vm.OnlinePlayers);
        processes.Running = new RunningServer(10, DateTime.Now); // and starts it again
        now = now.AddMinutes(5);
        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync();

        Assert.Single(SteamCmd.Installs);
        Assert.Equal([9], processes.Stopped);
    }

    /// <summary>
    /// A SteamCMD that hangs on the hourly check (it can, at login) kept SteamCMD "busy" for good,
    /// and every beat skips while it is: crash restarts and daily restarts of every server
    /// stopped without a word. The check gives up after UpdateCheckTimeout.
    /// </summary>
    [Fact]
    public async Task AutoUpdate_AHungCheck_GivesUp_AndAutomationCarriesOn()
    {
        WriteManifest(buildId: 1);
        SteamCmd.CheckHook = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 2;
        };
        var (vm, _, processes, _) = BuildWithServer();
        vm.AutoUpdate = true;
        vm.UpdateCheckTimeout = TimeSpan.FromMilliseconds(50);
        processes.Running = new RunningServer(9, DateTime.Now);

        var beat = vm.RunAutomationBeatAsync();
        Assert.Same(beat, await Task.WhenAny(beat, Task.Delay(TimeSpan.FromSeconds(10))));

        Assert.False(vm.IsSteamCmdBusy);
        Assert.Null(vm.LatestBuildId);
        Assert.Contains(vm.RecentEvents, e => e.Contains("Couldn't check for an update"));
    }

    /// <summary>
    /// Update on one server stops it first (up to 60 seconds); an hourly check of another server
    /// could start in that gap. The update then ran alongside it: the check's end cleared "SteamCMD
    /// busy" mid-update (buttons back, Cancel greyed out) and Cancel stopped the wrong run. The
    /// update now waits for the check.
    /// </summary>
    [Fact]
    public async Task Update_WhileAnotherServersHourlyCheckRuns_WaitsForIt()
    {
        WriteManifest(buildId: 1);
        var other = Path.Combine(_root, "Second Server");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(other).FullName, "IcarusServer.exe"), "");
        var store = new FakeStore();
        store.Profiles.Add(new DedicatedServerProfile { Name = "First", InstallFolder = _install });
        store.Profiles.Add(new DedicatedServerProfile { Name = "Second", InstallFolder = other, AutoUpdate = true, AutoRestartOnCrash = false });
        var (vm, _, processes, _) = Build(existingStore: store);
        processes.RunningFor = p => p.Name == "First" ? new RunningServer(9, DateTime.Now) : new RunningServer(20, DateTime.Now);
        vm.Tick();
        var answer = new TaskCompletionSource<int?>();
        SteamCmd.CheckHook = _ => answer.Task;
        Task? beat = null;
        processes.DuringStop = () => beat = vm.RunAutomationBeatAsync(); // the second server's hourly check starts

        var update = vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.NotNull(beat);
        Assert.Equal(1, SteamCmd.Checks);
        Assert.Empty(SteamCmd.Installs); // waiting for the check
        Assert.True(vm.IsSteamCmdBusy);

        answer.SetResult(1);
        await beat!;
        await update;

        Assert.Single(SteamCmd.Installs);
        Assert.False(vm.IsSteamCmdBusy);
    }

    /// <summary>A check leaves the SteamCMD box (still showing an update's output) titled as that update was.</summary>
    [Fact]
    public async Task CheckForUpdate_AfterAnUpdate_KeepsTheUpdatesTitleOnTheBox()
    {
        WriteManifest(buildId: 1);
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        SteamCmd.FailWith = "ERROR! Failed to install app '2089300' (Disk write failure)";
        await vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.Equal("Updating 'Icarus Dedicated Server' · failed", vm.SteamCmdTitle);

        SteamCmd.CheckHook = _ => throw new InvalidOperationException("SteamCMD isn't set up");
        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.ShowSteamCmdPanel);
        Assert.Equal("Updating 'Icarus Dedicated Server' · failed", vm.SteamCmdTitle);
        Assert.Contains("Couldn't check for an update", vm.StatusMessage);
    }

    private void AppendLogOrWrite(string line)
    {
        if (File.Exists(LogPath))
        {
            AppendLog(line);
        }
        else
        {
            WriteLog(line);
        }
    }

    private sealed class FakeNetwork : INetworkAddressService
    {
        public IReadOnlyList<NetworkAdapterInfo> Adapters { get; set; } = [];

        public Func<CancellationToken, Task<string>> PublicReply { get; set; } = _ => Task.FromResult("198.51.100.1");

        public int PublicCalls { get; private set; }

        public IReadOnlyList<NetworkAdapterInfo> ReadAdapters() => Adapters;

        public Task<string> FetchPublicIpReplyAsync(CancellationToken cancellationToken)
        {
            PublicCalls++;
            return PublicReply(cancellationToken);
        }
    }
}
