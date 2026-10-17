using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Phase 4: the Overview tab's automation, tiles and firewall card, against the same fakes — nothing is launched and the real firewall is never touched.</summary>
public sealed partial class LocalServerViewModelTests
{
    private string LogPath => DedicatedServerLayout.LogPath(_install, null, null);

    private void WriteLog(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, string.Join("\r\n", lines) + "\r\n");
    }

    private void AppendLog(params string[] lines) => File.AppendAllText(LogPath, string.Join("\r\n", lines) + "\r\n");

    private const string JoinLine = "[2026.08.12-17.31.51:477][117]LogNet: Join succeeded: MK-HATERS";

    private (LocalServerViewModel Vm, FakeStore Store, FakeProcesses Processes, FakeDialogs Dialogs) BuildWithServer(bool confirm = true)
    {
        var built = Build(pick: _install, confirm: confirm);
        built.Vm.AddExistingServerCommand.Execute(null);
        built.Processes.RunAfterStart = true;
        return built;
    }

    /// <summary>The server is seen running, then gone for the two beats it takes to call it stopped.</summary>
    private static async Task SeeItStopAsync(LocalServerViewModel vm, FakeProcesses processes)
    {
        processes.Running = null;
        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync();
    }

    [Fact]
    public void AutomationToggles_SaveStraightIntoTheProfile_AndSurviveASettingsSave()
    {
        var (vm, store, _, _) = BuildWithServer();
        Assert.True(vm.AutoRestartOnCrash, "crash restart is the one automation that starts on");

        vm.DailyRestartEnabled = true;
        vm.DailyRestartTime = "4:30";
        vm.AutoUpdate = true;
        vm.StartMinimized = true;
        vm.DailyRestartTime = "25:99";

        Assert.NotNull(vm.DailyRestartTimeError);
        var saved = store.Profiles.Single();
        Assert.True(saved.DailyRestartEnabled);
        Assert.Equal("04:30", saved.DailyRestartTime);
        Assert.True(saved.AutoUpdate);

        vm.SteamServerName = "Renamed";
        vm.SaveSettingsCommand.Execute(null);

        var after = store.Profiles.Single();
        Assert.Equal("Renamed", after.SteamServerName);
        Assert.True(after.DailyRestartEnabled);
        Assert.Equal("04:30", after.DailyRestartTime);
        Assert.True(after.AutoUpdate);
        Assert.True(after.StartMinimized);
    }

    /// <summary>A run whose log just stops is a crash and is started again; one that logged a clean exit is left alone.</summary>
    [Fact]
    public async Task Crash_IsRestarted_ButACleanExitIsNot()
    {
        var (vm, _, processes, _) = BuildWithServer();
        WriteLog("[2026.08.12-18.05.54:868][  1]LogInit: running");
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.RunAutomationBeatAsync();

        processes.Running = null;
        await vm.RunAutomationBeatAsync();
        Assert.Empty(processes.Started); // one missing beat could be the launcher handing over

        await vm.RunAutomationBeatAsync();
        Assert.Single(processes.Started);
        Assert.Contains("without a clean exit", vm.StatusMessage);

        await vm.RunAutomationBeatAsync(); // sees the new run (pid 42)
        AppendLog("[2026.08.12-18.08.55:542][741]LogCore: Engine exit requested (reason: ConsoleCtrl RequestExit)",
            "[2026.08.12-18.08.55:552][741]LogExit: Exiting.");
        await SeeItStopAsync(vm, processes);

        Assert.Single(processes.Started);
    }

    [Fact]
    public async Task Crash_WithCrashRestartOff_StaysStopped()
    {
        var (vm, _, processes, _) = BuildWithServer();
        vm.AutoRestartOnCrash = false;
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.RunAutomationBeatAsync();

        await SeeItStopAsync(vm, processes);

        Assert.Empty(processes.Started);
    }

    /// <summary>Stop in the app is never mistaken for a crash, even when the server had to be ended without its exit lines.</summary>
    [Fact]
    public async Task StopButton_IsNotACrash()
    {
        var (vm, _, processes, _) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now);
        vm.Tick();
        await vm.RunAutomationBeatAsync();
        processes.StopIsClean = false;

        await vm.StopCommand.ExecuteAsync(null);
        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync();

        Assert.Empty(processes.Started);
    }

    /// <summary>
    /// The review's case: Stop, then an Update that keeps SteamCMD busy for longer than the old
    /// 3-minute "intended stop" note lasted. The beat that finally sees the server gone must not
    /// call it a crash and start it again.
    /// </summary>
    [Fact]
    public async Task StopThenALongUpdate_IsStillNotACrash()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        var now = DateTime.Today.AddHours(12);
        vm.Clock = () => now;
        processes.Running = new RunningServer(9, DateTime.Now);
        vm.Tick();
        await vm.RunAutomationBeatAsync();
        processes.StopIsClean = false;

        await vm.UpdateServerCommand.ExecuteAsync(null); // asks, stops, updates
        now = now.AddMinutes(20);
        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync();

        Assert.Single(SteamCmd.Installs);
        Assert.Empty(processes.Started);
    }

    /// <summary>Right before automation starts a server it looks again: a server already running (started by hand meanwhile) isn't started twice.</summary>
    [Fact]
    public async Task CrashRestart_SkipsTheLaunchWhenItIsAlreadyRunningAgain()
    {
        var (vm, _, processes, _) = BuildWithServer();
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.RunAutomationBeatAsync();
        processes.Running = null;
        await vm.RunAutomationBeatAsync();
        // The beat still sees it gone, then someone starts it between that decision and the launch.
        var looks = 0;
        processes.FindRunningHook = () => ++looks == 1 ? null : new RunningServer(77, DateTime.Now);

        await vm.RunAutomationBeatAsync();

        Assert.Empty(processes.Started);
    }

    /// <summary>The daily restart waits while someone is online, then goes ahead at the end of the hour — with an update first when "update on restart" is on.</summary>
    [Fact]
    public async Task DailyRestart_WaitsForPlayers_ThenRestartsAndUpdates()
    {
        WriteManifest(buildId: 1);
        WriteProspect("Hard Elysium JULY");
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        vm.DailyRestartEnabled = true;
        vm.DailyRestartTime = "05:00";
        vm.UpdateOnRestart = true;
        var now = DateTime.Today.AddHours(5).AddSeconds(10);
        vm.Clock = () => now;
        WriteLog(JoinLine);
        processes.Running = new RunningServer(9, DateTime.Now.AddHours(-3));

        await vm.RunAutomationBeatAsync();
        Assert.Empty(processes.Stopped);
        Assert.Contains(vm.NextUp, l => l.Contains("waiting for 1 player"));

        now = DateTime.Today.AddHours(6).AddSeconds(5);
        await vm.RunAutomationBeatAsync();

        Assert.Equal([9], processes.Stopped);
        Assert.Equal([(_install, "public")], SteamCmd.Installs);
        Assert.Single(processes.Started);
        Assert.Contains(vm.Backups, b => b.Name.Contains("before-update"));

        await vm.RunAutomationBeatAsync();
        Assert.Single(processes.Started); // once a day
    }

    /// <summary>Auto-update asks Steam once an hour and installs while nobody is online.</summary>
    [Fact]
    public async Task AutoUpdate_ChecksHourly_AndInstallsWhenTheServerIsEmpty()
    {
        WriteManifest(buildId: 1);
        WriteProspect("Hard Elysium JULY");
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        vm.AutoUpdate = true;
        vm.Clock = () => DateTime.Today.AddHours(12);
        processes.Running = new RunningServer(9, DateTime.Now);

        await vm.RunAutomationBeatAsync(); // the hourly check finds a newer build
        Assert.Empty(SteamCmd.Installs);
        Assert.Equal(2, vm.LatestBuildId);

        await vm.RunAutomationBeatAsync();

        Assert.Equal([9], processes.Stopped);
        Assert.Equal([(_install, "public")], SteamCmd.Installs);
        Assert.Single(processes.Started);
    }

    [Fact]
    public async Task AutoUpdate_WaitsWhilePlayersAreOnline_AndChecksOnlyHourly()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = BuildWithServer();
        vm.AutoUpdate = true;
        var now = DateTime.Today.AddHours(12);
        vm.Clock = () => now;
        WriteLog(JoinLine);
        processes.Running = new RunningServer(9, DateTime.Now);

        await vm.RunAutomationBeatAsync();
        SteamCmd.LatestBuild = null; // a second question to Steam would now answer "no build"
        now = now.AddMinutes(30);
        await vm.RunAutomationBeatAsync();

        Assert.Empty(processes.Stopped);
        Assert.Empty(SteamCmd.Installs);
        Assert.Contains(vm.NextUp, l => l.Contains("installs as soon as nobody is online"));
    }

    [Fact]
    public async Task StartWithStarlink_StartsMarkedServersThatArentRunning()
    {
        var (vm, _, processes, _) = BuildWithServer();
        vm.StartWithApp = true;

        await vm.StartServersMarkedForStartupAsync();
        Assert.Single(processes.Started);

        await vm.StartServersMarkedForStartupAsync(); // already running now
        Assert.Single(processes.Started);
    }

    [Fact]
    public async Task StartMinimized_MinimizesTheConsoleOnceTheServerIsUp()
    {
        var (vm, _, processes, _) = BuildWithServer();
        vm.StartMinimized = true;

        await vm.StartCommand.ExecuteAsync(null);
        vm.AutomationTick();

        Assert.Equal([42], processes.Minimized);
        vm.AutomationTick();
        Assert.Single(processes.Minimized);
    }

    [Fact]
    public void Tiles_ShowPlayersMemoryAndPid()
    {
        var (vm, _, processes, _) = BuildWithServer();
        vm.MaxPlayersText = "6";
        WriteLog(JoinLine);
        processes.Running = new RunningServer(1234, DateTime.Now.AddMinutes(-90));

        vm.Tick();

        Assert.Equal("1 / 6", vm.PlayersTile);
        Assert.Equal("MK-HATERS", vm.PlayersHint);
        Assert.Equal("8.0 GB", vm.MemoryTile);
        Assert.Equal("PID 1234", vm.MemoryHint);
        Assert.StartsWith("1h 3", vm.UptimeTile);
    }

    // ---- Firewall ----

    private string ServerExe => DedicatedServerLayout.ShippingExePath(_install);

    /// <summary>Existing rules (like the user's own "Icarus Server UDP") count; only what's missing is added, scoped to the server's exe, after one confirmation.</summary>
    [Fact]
    public async Task Firewall_CountsExistingRules_AndAddsOnlyWhatsMissing()
    {
        Firewall.Rules.Add(new FirewallRule("Icarus Server UDP", true, true, true, ServerFirewall.Udp, "17777", ServerExe));
        Firewall.Rules.Add(new FirewallRule("Icarus Server TCP", true, true, true, ServerFirewall.Tcp, "17777", ServerExe));
        Firewall.Rules.Add(new FirewallRule("Some other game", true, true, true, ServerFirewall.Udp, "27015", @"C:\Games\other.exe"));
        var (vm, _, _, dialogs) = BuildWithServer();
        await vm.FirewallLoadTask;

        Assert.True(vm.FirewallIncludeTcp, "a TCP rule already exists, so TCP is carried over");
        Assert.Equal([true, false, true], vm.FirewallPorts.Select(p => p.Open));
        Assert.Contains("Icarus Server UDP", vm.FirewallPorts[0].Summary);

        await vm.OpenFirewallPortsCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Messages);
        var commands = Assert.Single(Firewall.Runs);
        var adds = commands.Where(c => c.Contains(" add rule ")).ToList();
        var add = Assert.Single(adds);
        Assert.Contains("protocol=UDP localport=27015", add);
        Assert.Contains($"program=\"{ServerExe}\"", add);
        Assert.Contains("Opened 1 firewall port", vm.StatusMessage);
    }

    [Fact]
    public async Task Firewall_ABlockRuleIsReported_AndNotPaperedOver()
    {
        Firewall.Rules.Add(new FirewallRule("icarusserver-win64-shipping.exe", true, true, false, ServerFirewall.Udp, "*", ServerExe));
        var (vm, _, _, dialogs) = BuildWithServer();
        await vm.FirewallLoadTask;

        Assert.True(vm.FirewallHasBlockRule);
        Assert.Contains("block rule", vm.FirewallSummary);

        await vm.OpenFirewallPortsCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Messages);
        Assert.Empty(Firewall.Runs);
    }

    [Fact]
    public async Task Firewall_PortsFollowTheServer_AndCanBeChangedHere()
    {
        var (vm, _, _, _) = BuildWithServer();
        await vm.FirewallLoadTask;
        Assert.Equal("17777", vm.FirewallGamePortText);

        vm.PortText = "17800";
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal("17800", vm.FirewallGamePortText);

        vm.FirewallQueryPortText = "27100";
        Assert.Contains("the server itself uses", vm.FirewallSummary);
        Assert.Equal(27100, vm.FirewallPorts[1].Port);
    }

    /// <summary>Rules Starlink made before the profile was renamed are still its own: they count, and Remove deletes them by their real names.</summary>
    [Fact]
    public async Task Firewall_RulesMadeUnderAnOldProfileName_AreStillFoundAndRemoved()
    {
        var (vm, _, _, _) = BuildWithServer();
        var id8 = vm.SelectedProfile!.Id.ToString("N")[..8];
        var oldName = $"Icarus Starlink - Old name ({id8}) - Game port UDP";
        Firewall.Rules.Add(new FirewallRule(oldName, true, true, true, ServerFirewall.Udp, "17777", ServerExe));
        vm.ProfileName = "New name";
        vm.SaveSettingsCommand.Execute(null);
        await vm.RefreshFirewallAsync(detectTcp: false);

        Assert.True(vm.FirewallPorts[0].Open);

        await vm.RemoveFirewallRulesCommand.ExecuteAsync(null);

        var commands = Assert.Single(Firewall.Runs);
        Assert.Contains(commands, c => c == $"advfirewall firewall delete rule name=\"{oldName}\"");
        Assert.DoesNotContain(commands, c => c.Contains(" add rule "));
    }

    /// <summary>A Private-only rule doesn't let players in while the PC is on a Public network.</summary>
    [Fact]
    public async Task Firewall_APrivateOnlyRule_IsNotOpenOnAPublicNetwork()
    {
        Firewall.Network = new FirewallNetwork(ServerFirewall.PublicProfile, FirewallOn: true);
        Firewall.Rules.Add(new FirewallRule("Icarus Server UDP", true, true, true, ServerFirewall.Udp, "17777,27015", ServerExe, ServerFirewall.PrivateProfile));
        var (vm, _, _, _) = BuildWithServer();
        await vm.FirewallLoadTask;

        Assert.All(vm.FirewallPorts, p => Assert.False(p.Open));
        Assert.Contains("blocked on your current network (Public)", vm.FirewallPorts[0].Summary);
        Assert.Contains("Public", vm.FirewallSummary);
    }

    /// <summary>Removing a server offers to remove the rules Starlink made for it; they'd be orphaned otherwise.</summary>
    [Fact]
    public async Task RemoveProfile_OffersToRemoveItsFirewallRules()
    {
        var (vm, store, _, dialogs) = BuildWithServer();
        var profile = vm.SelectedProfile!;
        Firewall.Rules.Add(new FirewallRule(ServerFirewall.RuleName(profile, "Game port", ServerFirewall.Udp), true, true, true, ServerFirewall.Udp, "17777", ServerExe));
        Firewall.Rules.Add(new FirewallRule("Icarus Server UDP", true, true, true, ServerFirewall.Udp, "17777", ServerExe));

        await vm.RemoveProfileCommand.ExecuteAsync(null);

        Assert.Empty(store.Profiles);
        Assert.Contains("Also remove the 1 firewall rule(s)", dialogs.Messages[1]);
        var commands = Assert.Single(Firewall.Runs);
        Assert.DoesNotContain(commands, c => c.Contains("Icarus Server UDP\""));
        Assert.Contains("were removed too", vm.StatusMessage);
    }

    [Fact]
    public async Task RemoveProfile_KeepingItsFirewallRules_LeavesThemAlone()
    {
        var (vm, store, _, dialogs) = BuildWithServer();
        Firewall.Rules.Add(new FirewallRule(ServerFirewall.RuleName(vm.SelectedProfile!, "Game port", ServerFirewall.Udp), true, true, true, ServerFirewall.Udp, "17777", ServerExe));
        dialogs.Answers.Enqueue(true);
        dialogs.Answers.Enqueue(false);

        await vm.RemoveProfileCommand.ExecuteAsync(null);

        Assert.Empty(store.Profiles);
        Assert.Empty(Firewall.Runs);
        Assert.Contains("were kept", vm.StatusMessage);
    }

    private sealed class FakeFirewall : IFirewallService
    {
        public List<FirewallRule> Rules { get; } = [];
        public List<IReadOnlyList<string>> Runs { get; } = [];
        public FirewallNetwork Network { get; set; } = FirewallNetwork.Unknown;

        public IReadOnlyList<FirewallRule> ReadInboundRules() => [.. Rules];

        public FirewallNetwork ReadCurrentNetwork() => Network;

        public Task<FirewallApplyResult> RunElevatedAsync(IReadOnlyList<string> netshCommands, CancellationToken cancellationToken = default)
        {
            Runs.Add(netshCommands);
            return Task.FromResult(new FirewallApplyResult(true, "ok"));
        }
    }
}
