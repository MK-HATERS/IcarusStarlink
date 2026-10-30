using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Server page's Local server tab against a fake install on disk — nothing is ever launched.</summary>
public sealed partial class LocalServerViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _install;

    public LocalServerViewModelTests()
    {
        _install = Path.Combine(_root, "Icarus Dedicated Server");
        Directory.CreateDirectory(Path.Combine(_install, "Icarus", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(_install, "IcarusServer.exe"), "");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string IniPath => DedicatedServerLayout.SettingsIniPath(_install, null);

    private FakeSteamCmd SteamCmd { get; } = new();

    private FakeSettings Settings { get; } = new();

    private FakeFirewall Firewall { get; } = new();

    private string BackupsRoot => Path.Combine(_root, "Backups");

    private string MergedPakPath => Path.Combine(_root, "Staged_Build", IcarusStarlink.PakIO.InstallManifestNames.MergedPakFileName);

    private (LocalServerViewModel Vm, FakeStore Store, FakeProcesses Processes, FakeDialogs Dialogs) Build(
        string? pick = null, bool confirm = true, string? pickExe = null, FakeStore? existingStore = null,
        FakeLibrary? library = null, FakePatchService? patches = null, FakePatchImporter? importer = null)
    {
        var store = existingStore ?? new FakeStore();
        var processes = new FakeProcesses();
        var dialogs = new FakeDialogs(confirm);
        var vm = new LocalServerViewModel(store, processes, dialogs, new ActivityLog(), SteamCmd, Settings, MergedPakPath, BackupsRoot, _ => pick, () => pickExe, startTimer: false, firewall: Firewall,
            library: library, patchService: patches, patchImporter: importer, network: Network)
        {
            StartPollDelay = TimeSpan.Zero,
            RunWorkInline = true,
            CopyToClipboard = Copied.Add,
        };
        SteamCmd.Chosen = () => Settings.Current.SteamCmdExePath;
        return (vm, store, processes, dialogs);
    }

    /// <summary>Picking a subfolder finds the install root, and an existing ServerSettings.ini is read into the form.</summary>
    [Fact]
    public void AddExistingServer_FromASubfolder_FindsTheRootAndReadsItsIni()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nSessionName=My World\r\nMaxPlayers=6\r\nAdminPassword=adm\r\nResumeProspect=True\r\nLastProspectName=Olympus_Base\r\n");
        var (vm, store, _, _) = Build(pick: Path.Combine(_install, "Icarus", "Binaries", "Win64"));

        vm.AddExistingServerCommand.Execute(null);

        var profile = Assert.Single(store.Profiles);
        Assert.Equal(_install, profile.InstallFolder);
        Assert.Same(profile, vm.SelectedProfile);
        Assert.Equal("My World", vm.SteamServerName);
        Assert.Equal("6", vm.MaxPlayersText);
        Assert.Equal("adm", vm.AdminPassword);
        Assert.False(vm.AdminPasswordIsEmpty);
        Assert.True(vm.ResumeProspect);
        Assert.Equal("Olympus_Base", vm.LastProspectName);
        Assert.Equal("17777", vm.PortText);
    }

    /// <summary>Settings › Maintenance › Backups to keep › Server backups: a server added now starts with that count (its own Backups tab shows it), and old servers keep theirs.</summary>
    [Fact]
    public void AddExistingServer_StartsWithTheServerBackupsSetting()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nSessionName=My World\r\n");
        Settings.Current.ServerBackupsKept = 4;
        var (vm, store, _, _) = Build(pick: _install);

        vm.AddExistingServerCommand.Execute(null);

        Assert.Equal(4, Assert.Single(store.Profiles).BackupsToKeep);
        Assert.Equal("4", vm.BackupsToKeepText);
    }

    /// <summary>A 0 in settings.json can't give a new server "keep every backup" or "keep none": it's held to 1..50.</summary>
    [Fact]
    public void AddExistingServer_ServerBackupsSettingOfZero_StartsAtOne()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nSessionName=My World\r\n");
        Settings.Current.ServerBackupsKept = 0;
        var (vm, store, _, _) = Build(pick: _install);

        vm.AddExistingServerCommand.Execute(null);

        Assert.Equal(1, Assert.Single(store.Profiles).BackupsToKeep);
    }

    [Fact]
    public void AddExistingServer_NoServerThere_SaysSoAndAddsNothing()
    {
        var (vm, store, _, _) = Build(pick: _root + "_nothing");

        vm.AddExistingServerCommand.Execute(null);

        Assert.Empty(store.Profiles);
        Assert.Contains("No IcarusServer.exe", vm.StatusMessage);
    }

    /// <summary>The documented first-launch bug (no ini until the first lobby): Save writes one, with exactly the form's values and the game's defaults left blank.</summary>
    [Fact]
    public void Save_WithNoIniYet_WritesOneTheGameCanRead()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.AdminPassword = "s3cret";
        vm.JoinPassword = "friends";
        vm.MaxPlayersText = "4";
        vm.ShutdownIfEmptyForText = "-1";

        vm.SaveSettingsCommand.Execute(null);

        Assert.Null(vm.ValidationMessage);
        var written = ServerSettingsIni.Load(IniPath).Read();
        Assert.Equal("s3cret", written.AdminPassword);
        Assert.Equal("friends", written.JoinPassword);
        Assert.Equal(4, written.MaxPlayers);
        Assert.Equal(-1, written.ShutdownIfEmptyFor);
        // A blank ResumeProspect fails the game's import: unset is written as the game's default.
        Assert.True(written.ResumeProspect);
        Assert.Equal("Saved.", vm.StatusMessage);
    }

    [Theory]
    [InlineData("9", "17777", "27015", "Max players")]
    [InlineData("4", "0", "27015", "Game port")]
    [InlineData("4", "27015", "27015", "must differ")]
    [InlineData("4", "17777", "abc", "Query port")]
    public void Save_OutOfRangeValues_AreRefusedWithTheReasonAndNothingIsWritten(string maxPlayers, string port, string queryPort, string expected)
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.MaxPlayersText = maxPlayers;
        vm.PortText = port;
        vm.QueryPortText = queryPort;

        vm.SaveSettingsCommand.Execute(null);

        Assert.Contains(expected, vm.ValidationMessage);
        Assert.False(File.Exists(IniPath));
    }

    /// <summary>Start saves first (so the ini exists before the server boots), then launches with the form's launch options.</summary>
    [Fact]
    public async Task Start_SavesThenLaunchesWithTheFormsOptions()
    {
        var (vm, store, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.SteamServerName = "Friends server";
        vm.PortText = "17778";
        vm.QueryPortText = "27016";
        processes.RunAfterStart = true;

        await vm.StartCommand.ExecuteAsync(null);

        var started = Assert.Single(processes.Started);
        Assert.Equal("Friends server", started.SteamServerName);
        Assert.Equal(17778, started.Port);
        Assert.True(File.Exists(IniPath));
        Assert.Equal(17778, store.Profiles.Single().Port);
        Assert.True(vm.IsRunning);
        Assert.StartsWith("RUNNING", vm.RunStateText);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_AsksFirstAndReportsAForcedEnd()
    {
        var (vm, _, processes, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(1234, DateTime.Now.AddMinutes(-5));
        vm.Tick();
        processes.StopIsClean = false;

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Messages);
        Assert.Equal([1234], processes.Stopped);
        Assert.Contains("was ended", vm.StatusMessage);
        Assert.False(vm.IsRunning);
    }

    /// <summary>Like the developer's own server (SaveGameOnExit=False, GameSaveFrequency=3): Stop must not promise a save, and automation that restarts it on its own is flagged.</summary>
    [Fact]
    public async Task Stop_WithSaveOnExitOff_WarnsThatProgressSinceTheAutosaveIsLost()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nGameSaveFrequency=3.000000\r\nSaveGameOnExit=False\r\n");
        var (vm, _, processes, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(1234, DateTime.Now);
        vm.Tick();

        await vm.StopCommand.ExecuteAsync(null);

        var message = Assert.Single(dialogs.Messages);
        Assert.DoesNotContain("saves on exit", message);
        Assert.Contains("'Save on exit' is OFF", message);
        Assert.Contains("every 3 minutes", message);

        Assert.Null(vm.SaveOnExitWarning);
        vm.DailyRestartEnabled = true;
        Assert.Contains("every automatic restart", vm.SaveOnExitWarning);
        vm.SaveGameOnExit = true;
        Assert.Null(vm.SaveOnExitWarning);
    }

    /// <summary>Regression: Restart re-read the selected server after the stop, so picking another one during the wait stopped A and started B.</summary>
    [Fact]
    public async Task Restart_AnotherServerSelectedDuringTheStop_DoesNotStartIt()
    {
        var (vm, store, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var first = vm.SelectedProfile!;
        var other = new DedicatedServerProfile { Name = "Other", InstallFolder = Path.Combine(_root, "Other") };
        store.Save(other);
        vm.Profiles.Add(other);
        vm.SelectedProfile = first;
        processes.Running = new RunningServer(1234, DateTime.Now);
        vm.Tick();
        processes.DuringStop = () => vm.SelectedProfile = other;

        await vm.RestartCommand.ExecuteAsync(null);

        Assert.Equal([1234], processes.Stopped);
        Assert.Empty(processes.Started);
        Assert.Contains("wasn't started again", vm.StatusMessage);
    }

    [Fact]
    public async Task Stop_Declined_LeavesTheServerAlone()
    {
        var (vm, _, processes, _) = Build(pick: _install, confirm: false);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(1234, DateTime.Now);
        vm.Tick();

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Empty(processes.Stopped);
        Assert.True(vm.IsRunning);
    }

    /// <summary>The log panel follows the server's own log file, hides engine chatter, and filters by text.</summary>
    [Fact]
    public void Tick_TailsTheLogAndAppliesTheFilters()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var log = DedicatedServerLayout.LogPath(_install, null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "LogStreaming: Display: noise\r\nLogNet: Join succeeded: James\r\nLogIcarus: Display: Loading prospect Olympus_Base\r\n");

        vm.Tick();

        Assert.Equal(["LogNet: Join succeeded: James", "LogIcarus: Display: Loading prospect Olympus_Base"], vm.LogLines);

        vm.HideNoise = false;
        Assert.Equal(3, vm.LogLines.Count);

        vm.LogFilter = "join";
        Assert.Equal(["LogNet: Join succeeded: James"], vm.LogLines);
    }

    [Fact]
    public async Task RemoveProfile_RemovesOnlyTheListEntry()
    {
        var (vm, store, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        await vm.RemoveProfileCommand.ExecuteAsync(null);

        Assert.Empty(store.Profiles);
        Assert.Null(vm.SelectedProfile);
        Assert.True(File.Exists(Path.Combine(_install, "IcarusServer.exe")));
    }

    /// <summary>The saved server list couldn't be read at startup: the page says so, and a Save doesn't pretend it worked.</summary>
    [Fact]
    public void UnreadableServerList_IsReported_AndSavesSaySoInsteadOfSaved()
    {
        var store = new FakeStore { LoadProblem = "Your saved server list couldn't be read." };
        var (vm, _, _, _) = Build(pick: _install, existingStore: store);
        Assert.Equal("Your saved server list couldn't be read.", vm.StatusMessage);

        vm.AddExistingServerCommand.Execute(null);
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal("Your saved server list couldn't be read.", vm.StatusMessage);
    }

    // ---- Phase 2: SteamCMD ----

    [Fact]
    public async Task InstallNewServer_RunsSteamCmdIntoAnEmptyFolderAndAddsTheProfileWithAnIni()
    {
        var target = Path.Combine(_root, "NewServer");
        SteamCmd.CreateServerOnInstall = true;
        var (vm, store, _, dialogs) = Build(pick: target);

        await vm.InstallNewServerCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Messages);
        Assert.Equal([(target, "public")], SteamCmd.Installs);
        var profile = Assert.Single(store.Profiles);
        Assert.Equal(target, profile.InstallFolder);
        Assert.True(File.Exists(DedicatedServerLayout.SettingsIniPath(target, null)));
        Assert.Contains("Installed the server", vm.StatusMessage);
    }

    /// <summary>A non-empty folder gets the server in a subfolder, never mixed into whatever is already there.</summary>
    [Fact]
    public async Task InstallNewServer_IntoANonEmptyFolder_UsesASubfolder()
    {
        var picked = Path.Combine(_root, "Games");
        Directory.CreateDirectory(picked);
        File.WriteAllText(Path.Combine(picked, "other.txt"), "x");
        SteamCmd.CreateServerOnInstall = true;
        var (vm, _, _, _) = Build(pick: picked);

        await vm.InstallNewServerCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(picked, "IcarusServer"), Assert.Single(SteamCmd.Installs).Folder);
    }

    [Fact]
    public async Task InstallNewServer_SteamCmdFails_AddsNothingAndSaysWhy()
    {
        SteamCmd.FailWith = "ERROR! Failed to install app '2089300' (Disk write failure)";
        var (vm, store, _, _) = Build(pick: Path.Combine(_root, "NewServer"));

        await vm.InstallNewServerCommand.ExecuteAsync(null);

        Assert.Empty(store.Profiles);
        Assert.Contains("Disk write failure", vm.StatusMessage);
    }

    [Fact]
    public async Task CheckForUpdate_ComparesTheInstalledManifestWithSteam()
    {
        WriteManifest(buildId: 25621760);
        SteamCmd.LatestBuild = 25700000;
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        Assert.Equal(25621760, vm.InstalledBuildId);
        Assert.False(vm.UpdateAvailable);

        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.UpdateAvailable);
        Assert.Contains("update available (build 25700000)", vm.BuildSummary);
    }

    /// <summary>Update while running: asks, stops the server, backs up its worlds, then runs SteamCMD on the profile's branch (how a branch switch is applied).</summary>
    [Fact]
    public async Task UpdateServer_WhileRunning_StopsBacksUpAndUpdatesOnTheChosenBranch()
    {
        WriteManifest(buildId: 1);
        WriteProspect("Hard Elysium JULY");
        var (vm, _, processes, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(7, DateTime.Now);
        vm.Tick();
        vm.Branch = "experimental";
        Assert.True(vm.UpdateAvailable, "a different branch than the installed one needs an Update");

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Messages);
        Assert.Equal([7], processes.Stopped);
        Assert.Equal([(_install, "experimental")], SteamCmd.Installs);
        var backup = Assert.Single(vm.Backups);
        Assert.Contains("before-update", backup.Name);
    }

    /// <summary>Cancel in the SteamCMD box stops a long download; the page says how to finish later.</summary>
    [Fact]
    public async Task UpdateServer_CanBeCancelled()
    {
        WriteManifest(buildId: 1);
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        SteamCmd.WaitForCancel = true;

        var update = vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.True(vm.IsSteamCmdBusy);
        Assert.True(vm.CancelSteamCmdCommand.CanExecute(null));

        // An app update asks first, and is told to wait.
        var replies = WeakReferenceMessenger.Default.Send(new IcarusStarlink.App.Messages.BackgroundWorkRequestMessage()).Responses;
        Assert.Contains(replies, r => r.StartsWith("SteamCMD", StringComparison.Ordinal));

        vm.CancelSteamCmdCommand.Execute(null);
        await update;

        Assert.False(vm.IsSteamCmdBusy);
        Assert.Contains("Cancelled", vm.StatusMessage);
        Assert.False(vm.CancelSteamCmdCommand.CanExecute(null));
    }

    /// <summary>
    /// Regression: the first "Install SteamCMD" set IsSteamCmdBusy (which tells the UI to re-ask
    /// Cancel) before the run's token existed, so the UI saw Cancel greyed out for the whole run.
    /// A cancelled download also used to have already cleared the user's own steamcmd.exe.
    /// </summary>
    [Fact]
    public async Task InstallSteamCmd_CancelIsLiveFromTheStart_AndACancelKeepsTheUsersOwnSteamCmd()
    {
        var mine = Path.Combine(_root, "steamcmd.exe");
        File.WriteAllText(mine, "");
        Settings.Current.SteamCmdExePath = mine;
        var (vm, _, _, _) = Build();
        SteamCmd.WaitForCancel = true;
        var cancelSeen = new List<bool>();
        vm.CancelSteamCmdCommand.CanExecuteChanged += (_, _) => cancelSeen.Add(vm.CancelSteamCmdCommand.CanExecute(null));

        var install = vm.InstallSteamCmdCommand.ExecuteAsync(null);

        Assert.True(cancelSeen.Last(), "the UI was last told Cancel can't run");
        vm.CancelSteamCmdCommand.Execute(null);
        await install;

        Assert.Equal(mine, Settings.Current.SteamCmdExePath);
        Assert.Contains("Cancelled", vm.StatusMessage);
    }

    /// <summary>Regression: Install SteamCMD wasn't told when another SteamCMD run or a start/stop began, so it stayed clickable mid-run.</summary>
    [Fact]
    public async Task SteamCmdAndServerBusy_GreyOutInstallSteamCmdAndUpdate()
    {
        WriteManifest(buildId: 1);
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var installSeen = new List<bool>();
        vm.InstallSteamCmdCommand.CanExecuteChanged += (_, _) => installSeen.Add(vm.InstallSteamCmdCommand.CanExecute(null));
        var updateSeen = new List<bool>();
        vm.UpdateServerCommand.CanExecuteChanged += (_, _) => updateSeen.Add(vm.UpdateServerCommand.CanExecute(null));

        SteamCmd.WaitForCancel = true;
        var check = vm.UpdateServerCommand.ExecuteAsync(null);
        Assert.False(installSeen.LastOrDefault(true), "Install SteamCMD wasn't greyed out during an update");
        vm.CancelSteamCmdCommand.Execute(null);
        await check;

        updateSeen.Clear();
        vm.IsBusy = true;
        Assert.False(updateSeen.LastOrDefault(true), "Update wasn't greyed out while the server starts or stops");
    }

    /// <summary>The user's first install: no server in the list yet, so no Log tab — the SteamCMD box itself must show the output and keep it after the run until dismissed.</summary>
    [Fact]
    public async Task InstallNewServer_ShowsSteamCmdOutputInItsOwnBoxWithNoServerListed()
    {
        SteamCmd.CreateServerOnInstall = true;
        var (vm, _, _, _) = Build(pick: Path.Combine(_root, "NewServer"));
        Assert.False(vm.ShowSteamCmdPanel);

        await vm.InstallNewServerCommand.ExecuteAsync(null);

        Assert.True(vm.ShowSteamCmdPanel);
        Assert.Contains(vm.SteamCmdOutput, l => l.Contains("progress: 50.00"));
        Assert.True(vm.IsSteamCmdIdle);

        vm.DismissSteamCmdOutputCommand.Execute(null);
        Assert.False(vm.ShowSteamCmdPanel);
    }

    // ---- Which SteamCMD ----

    /// <summary>No SteamCMD yet: installing a server first asks to put Starlink's copy in its own folder, then carries on.</summary>
    [Fact]
    public async Task InstallNewServer_WithNoSteamCmd_AsksToInstallItFirst()
    {
        SteamCmd.OwnExe = null;
        SteamCmd.CreateServerOnInstall = true;
        var (vm, store, _, dialogs) = Build(pick: Path.Combine(_root, "NewServer"));
        Assert.False(vm.HasSteamCmd);

        await vm.InstallNewServerCommand.ExecuteAsync(null);

        Assert.Equal(2, dialogs.Messages.Count);
        Assert.Contains("SteamCMD isn't set up yet", dialogs.Messages[1]);
        Assert.Equal(1, SteamCmd.OwnInstalls);
        Assert.True(vm.HasSteamCmd);
        Assert.Single(store.Profiles);
    }

    [Fact]
    public async Task CheckForUpdate_WithNoSteamCmdAndTheOfferDeclined_DoesNothing()
    {
        SteamCmd.OwnExe = null;
        var (vm, _, _, _) = Build(pick: _install, confirm: false);
        vm.AddExistingServerCommand.Execute(null);

        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Equal(0, SteamCmd.OwnInstalls);
        Assert.Contains("Use my SteamCMD", vm.StatusMessage);
    }

    /// <summary>The SteamCMD box: full with no server yet; one "not set up" line once a server exists (Set up… opens it); a declined update opens it too.</summary>
    [Fact]
    public async Task SteamCmdBox_FoldsToOneLineOnceAServerExists_AndOpensWhenNeeded()
    {
        SteamCmd.OwnExe = null;
        var (vm, _, _, _) = Build(pick: _install, confirm: false);
        Assert.True(vm.ShowSteamCmdBox);
        Assert.False(vm.ShowSteamCmdLine);

        vm.AddExistingServerCommand.Execute(null);
        Assert.False(vm.ShowSteamCmdBox);
        Assert.True(vm.ShowSteamCmdLine);

        vm.ExpandSteamCmdSetupCommand.Execute(null);
        Assert.True(vm.ShowSteamCmdBox);

        var (again, _, _, _) = Build(pick: _install, confirm: false);
        again.AddExistingServerCommand.Execute(null);
        Assert.False(again.ShowSteamCmdBox);
        await again.UpdateServerCommand.ExecuteAsync(null);
        Assert.True(again.ShowSteamCmdBox);
        Assert.Contains("needed to install or update", again.StatusMessage);
    }

    /// <summary>"Use my SteamCMD…" stores the user's own steamcmd.exe and only accepts a file actually called steamcmd.exe.</summary>
    [Fact]
    public void UseExistingSteamCmd_StoresTheChosenExe()
    {
        var own = Path.Combine(_root, "steamcmd", "steamcmd.exe");
        var wrong = Path.Combine(_root, "steamcmd", "notepad.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "");
        File.WriteAllText(wrong, "");
        SteamCmd.OwnExe = null;

        var (refused, _, _, _) = Build(pickExe: wrong);
        refused.UseExistingSteamCmdCommand.Execute(null);
        Assert.Null(Settings.Current.SteamCmdExePath);
        Assert.Contains("isn't steamcmd.exe", refused.StatusMessage);

        var (vm, _, _, _) = Build(pickExe: own);
        vm.UseExistingSteamCmdCommand.Execute(null);

        Assert.Equal(own, Settings.Current.SteamCmdExePath);
        Assert.True(vm.HasSteamCmd);
        Assert.True(vm.UsingOwnSteamCmd);
        Assert.Equal($"SteamCMD: {own}", vm.SteamCmdSummary);
    }

    /// <summary>The user's real server manifest records "LauncherPath" — adopting the server reuses that SteamCMD when none is set up.</summary>
    [Fact]
    public void AddExistingServer_ReusesTheSteamCmdThatInstalledIt()
    {
        var theirs = Path.Combine(_root, "Personal", "steamcmd", "steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(theirs)!);
        File.WriteAllText(theirs, "");
        var manifest = DedicatedServerLayout.AppManifestPath(_install);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, $"\"AppState\"\n{{\n\t\"appid\"\t\t\"2089300\"\n\t\"LauncherPath\"\t\t\"{theirs.Replace(@"\", @"\\")}\"\n\t\"buildid\"\t\t\"25621760\"\n}}\n");
        SteamCmd.OwnExe = null;
        var (vm, _, _, _) = Build(pick: _install);

        vm.AddExistingServerCommand.Execute(null);

        Assert.Equal(theirs, Settings.Current.SteamCmdExePath);
        Assert.Contains("Using the SteamCMD that installed it", vm.StatusMessage);
    }

    // ---- Phase 3: mods, backups, admins, players ----

    [Fact]
    public void InstallMergedPak_CopiesThePakAndManifestAndRefusesWhileRunning()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
        File.WriteAllBytes(MergedPakPath, [1, 2, 3]);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(MergedPakPath)!, "ISL-Merged.txt"), "Includes the following mods:\nA\n");
        var (vm, _, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(1, DateTime.Now);
        vm.Tick();

        vm.InstallMergedPakCommand.Execute(null);
        Assert.Contains("Stop the server", vm.StatusMessage);
        Assert.Empty(vm.ServerPaks);

        // The merged pak under its other name is replaced, not left loading beside the new one. No
        // ArcFission Library on this server: the merge goes in under the plain name.
        Directory.CreateDirectory(DedicatedServerLayout.PakModsFolder(_install));
        File.WriteAllBytes(Path.Combine(DedicatedServerLayout.PakModsFolder(_install), "zz_zz_ISL-Merged_9999_P.pak"), [9]);
        processes.Running = null;
        vm.Tick();
        vm.InstallMergedPakCommand.Execute(null);

        Assert.Equal(["ISL-Merged_P.pak"], vm.ServerPaks.Select(p => p.FileName));
        Assert.True(File.Exists(Path.Combine(DedicatedServerLayout.PakModsFolder(_install), "ISL-Merged.txt")));
    }

    /// <summary>
    /// The server's own ArcFission Library writes a new data pak number on each rebuild and can't
    /// delete the old ones while the server runs: copying the merge moves those into the server's
    /// backups (never deleted), keeps the one the library's state.lua names, and doesn't call that
    /// one a stray merged pak in the question.
    /// </summary>
    [Fact]
    public void InstallMergedPak_MovesTheServersOlderArcFissionCopiesIntoItsBackups()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
        File.WriteAllBytes(MergedPakPath, [1, 2, 3]);
        var modsFolder = DedicatedServerLayout.PakModsFolder(_install);
        Directory.CreateDirectory(modsFolder);
        File.WriteAllBytes(Path.Combine(modsFolder, "ZZ_ArcFission_Data_1_P.pak"), [1]);
        File.WriteAllBytes(Path.Combine(modsFolder, "ZZ_ArcFission_Data_2_P.pak"), [2]);
        var library = Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "ArcFissionLibrary");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "enabled.txt"), "");
        File.WriteAllText(Path.Combine(library, "state.lua"), "return {\n    [\"gen\"] = 2,\n}\n");
        var (vm, _, _, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        dialogs.Messages.Clear();

        vm.InstallMergedPakCommand.Execute(null);

        Assert.Equal(["ZZ_ArcFission_Data_2_P.pak", "zz_zz_ISL-Merged_9999_P.pak"], vm.ServerPaks.Select(p => p.FileName).Order(StringComparer.Ordinal));
        var question = Assert.Single(dialogs.Messages);
        Assert.Contains("1 older copy of ArcFission's data", question);
        Assert.DoesNotContain("left alone", question);
        Assert.Single(Directory.GetFiles(BackupsRoot, "*ZZ_ArcFission_Data_1_P.pak", SearchOption.AllDirectories));
    }

    /// <summary>
    /// An older ArcFission copy that's gone by the time the question is answered (the server's
    /// library or the user took it off) is already done: the page used to say "Couldn't copy the
    /// pak" although the copy had worked (review 2026-10-26).
    /// </summary>
    [Fact]
    public void InstallMergedPak_AnOlderArcFissionCopyGoneBeforeTheMove_StillSaysInstalled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
        File.WriteAllBytes(MergedPakPath, [1, 2, 3]);
        var modsFolder = DedicatedServerLayout.PakModsFolder(_install);
        Directory.CreateDirectory(modsFolder);
        var older = Path.Combine(modsFolder, "ZZ_ArcFission_Data_1_P.pak");
        File.WriteAllBytes(older, [1]);
        File.WriteAllBytes(Path.Combine(modsFolder, "ZZ_ArcFission_Data_2_P.pak"), [2]);
        var library = Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "ArcFissionLibrary");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "enabled.txt"), "");
        var (vm, _, _, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        dialogs.WhileAsking = () => File.Delete(older);

        vm.InstallMergedPakCommand.Execute(null);

        Assert.StartsWith("Installed zz_zz_ISL-Merged_9999_P.pak", vm.StatusMessage);
        Assert.Equal(["ZZ_ArcFission_Data_2_P.pak", "zz_zz_ISL-Merged_9999_P.pak"], vm.ServerPaks.Select(p => p.FileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Ue4ss_InstallFromGameThenMatch_CopiesTheModsOnInTheGame()
    {
        var gameWin64 = MakeGameWithUe4ss(("StorageLink", true), ("NearbyCrafting", false));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        Assert.False(vm.ServerHasUe4ss);
        Assert.Equal(["NearbyCrafting", "StorageLink"], vm.Ue4ssModRows.Select(m => m.Name));
        Assert.All(vm.Ue4ssModRows, r => Assert.Equal(ServerModState.NotOnServer, r.State));

        vm.InstallUe4ssOnServerCommand.Execute(null);
        Assert.True(vm.ServerHasUe4ss);

        vm.MatchServerToGameCommand.Execute(null);

        var storage = vm.Ue4ssModRows.Single(r => r.Name == "StorageLink");
        Assert.Equal(ServerModState.Same, storage.State);
        Assert.True(storage.ServerOn);
        Assert.Equal(ServerModState.NotOnServer, vm.Ue4ssModRows.Single(r => r.Name == "NearbyCrafting").State); // off in the game: not copied
        Assert.True(File.Exists(Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "StorageLink", "Scripts", "main.lua")));
    }

    /// <summary>
    /// User request 2026-10-27: the same mod with different settings in the game and on the server
    /// read as "Same as your game". The row says so, and Match settings… copies the chosen copy's
    /// config the other way, backing the replaced file up first.
    /// </summary>
    [Fact]
    public void Ue4ss_DifferentSettings_AreFlagged_AndMatchSettingsCopiesTheChosenCopy()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var gameMod = Path.Combine(gameWin64, "ue4ss", "Mods", "MkHatersTools");
        File.WriteAllText(Path.Combine(gameMod, "config.lua"), "return { Range = 10 }");
        var (vm, _, _, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        vm.MatchServerToGameCommand.Execute(null);
        var serverMod = Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "MkHatersTools");
        File.WriteAllText(Path.Combine(serverMod, "config.lua"), "return { Range = 50 }");
        vm.OnPageShown();

        var row = vm.Ue4ssModRows.Single();
        Assert.Equal(ServerModState.Same, row.State);
        Assert.True(row.ConfigDiffers);
        Assert.Equal("Same mod, different settings", row.StateText);
        Assert.Equal("Settings differ: config.lua", row.ConfigDiffersText);
        Assert.False(row.IsSame);

        // Cancelled: nothing changes.
        dialogs.ThreeWayAnswer = null;
        vm.MatchUe4ssModSettingsCommand.Execute(row);
        Assert.Equal("return { Range = 50 }", File.ReadAllText(Path.Combine(serverMod, "config.lua")));
        Assert.Equal("return { Range = 10 }", File.ReadAllText(Path.Combine(gameMod, "config.lua")));

        // The server's settings into the game; the game's old file is backed up.
        dialogs.ThreeWayAnswer = false;
        vm.MatchUe4ssModSettingsCommand.Execute(row);

        Assert.Equal("return { Range = 50 }", File.ReadAllText(Path.Combine(gameMod, "config.lua")));
        Assert.Contains("Your game now has", vm.StatusMessage);
        var saved = Assert.Single(Directory.GetFiles(Path.Combine(BackupsRoot, "UE4SS-Mod-Configs"), "config.lua", SearchOption.AllDirectories));
        Assert.Equal("return { Range = 10 }", File.ReadAllText(saved));
        row = vm.Ue4ssModRows.Single();
        Assert.False(row.ConfigDiffers);
        Assert.Equal("Same as your game", row.StateText);
        Assert.True(row.IsSame);
    }

    /// <summary>
    /// User report 2026-10-23: a mod updated in the game stayed old on the server and the page never
    /// said so. Now the row says "Server out of date", the tab says how many, and Update keeps the
    /// server's own config and on/off.
    /// </summary>
    [Fact]
    public void Ue4ss_ServerCopyOlder_IsFlagged_AndUpdateKeepsTheServersConfigAndOnOff()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var gameMod = Path.Combine(gameWin64, "ue4ss", "Mods", "MkHatersTools");
        File.WriteAllText(Path.Combine(gameMod, "Scripts", "main.lua"), "local VERSION = \"1.0.5\"\nprint(VERSION)");
        File.WriteAllText(Path.Combine(gameMod, "config.lua"), "return { Range = 10 }");
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        vm.MatchServerToGameCommand.Execute(null);

        // The server gets its own settings, is switched off there, and then the game's copy moves on.
        var serverMod = Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "MkHatersTools");
        File.WriteAllText(Path.Combine(serverMod, "config.lua"), "return { Range = 50 }");
        vm.Ue4ssModRows.Single().ServerOn = false;
        File.WriteAllText(Path.Combine(gameMod, "Scripts", "main.lua"), "local VERSION = \"1.0.6\"\nprint(VERSION)");
        vm.OnPageShown();

        var row = vm.Ue4ssModRows.Single();
        Assert.Equal(ServerModState.ServerOutOfDate, row.State);
        Assert.Equal("Server out of date", row.StateText);
        Assert.Equal("v1.0.6 · on", row.GameText);
        Assert.Equal("v1.0.5 · off", row.ServerText);
        Assert.Equal("Mods (1 to update)", vm.ModsTabHeader);

        vm.CopyUe4ssModToServerCommand.Execute(row);

        row = vm.Ue4ssModRows.Single();
        Assert.Equal(ServerModState.Same, row.State);
        Assert.False(row.ServerOn); // still off on the server
        Assert.Equal("return { Range = 50 }", File.ReadAllText(Path.Combine(serverMod, "config.lua")));
        Assert.Contains("1.0.6", File.ReadAllText(Path.Combine(serverMod, "Scripts", "main.lua")));
        Assert.Equal("Mods", vm.ModsTabHeader);
    }

    /// <summary>
    /// User report 2026-10-24: unticking a mod on the server, or removing it from the Library, left
    /// the row reading as before (even after a restart: unticking only switches it off). The row now
    /// re-reads at once and says off; a mod gone from the game says Remove takes it off.
    /// </summary>
    [Fact]
    public void Ue4ss_UntickingReadsBackOff_AndAModGoneFromTheGameSaysRemoveTakesItOff()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        vm.MatchServerToGameCommand.Execute(null);

        vm.Ue4ssModRows.Single().ServerOn = false;

        var row = vm.Ue4ssModRows.Single();
        Assert.False(row.ServerOn);
        Assert.EndsWith("off", row.ServerText);
        Assert.False(row.Comparison.OnOnServer);

        // Removed from the Library: the game's copy is gone, the server's stays until Remove.
        Directory.Delete(Path.Combine(gameWin64, "ue4ss", "Mods", "MkHatersTools"), recursive: true);
        vm.RefreshServerModsCommand.Execute(null);

        row = vm.Ue4ssModRows.Single();
        Assert.Equal(ServerModState.OnlyOnServer, row.State);
        Assert.True(row.NeedsAttention);
        Assert.Contains("Remove", row.StateText);
    }

    [Fact]
    public void BackUpNow_ThenRestore_PutsTheWorldBackAndKeepsASafetyCopy()
    {
        WriteProspect("World A", content: "{\"v\":1}");
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.BackUpNowCommand.Execute(null);
        var backup = Assert.Single(vm.Backups);
        WriteProspect("World A", content: "{\"v\":2,\"broken\":true}");

        vm.SelectedBackup = backup;
        vm.RestoreSelectedBackupCommand.Execute(null);

        Assert.Equal("{\"v\":1}", File.ReadAllText(Path.Combine(DedicatedServerLayout.ProspectsFolder(_install, null), "World A.json")));
        Assert.Equal(2, vm.Backups.Count);
        Assert.Contains(vm.Backups, b => b.Name.Contains("before-restore"));
    }

    /// <summary>The backups folder carries the profile's name; renaming the profile renames the folder instead of starting an empty one.</summary>
    [Fact]
    public async Task RenamingAProfile_KeepsItsBackups()
    {
        WriteProspect("World A");
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        await vm.BackUpNowCommand.ExecuteAsync(null);
        var oldFolder = vm.BackupsFolderPath;

        vm.ProfileName = "Friends & family";
        vm.SaveSettingsCommand.Execute(null);
        vm.SelectedProfile = null;
        vm.SelectedProfile = vm.Profiles.Single();

        Assert.Single(vm.Backups);
        Assert.NotEqual(oldFolder, vm.BackupsFolderPath);
        Assert.EndsWith("Friendsfamily_" + vm.SelectedProfile.Id.ToString("N")[..8], vm.BackupsFolderPath);
        Assert.False(Directory.Exists(oldFolder));
    }

    /// <summary>Only the newest N of each kind are kept.</summary>
    [Fact]
    public async Task BackUpNow_KeepsOnlyTheNewestOfEachKind()
    {
        WriteProspect("World A");
        var (vm, store, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.BackupsToKeepText = "2";
        Assert.Equal(2, store.Profiles.Single().BackupsToKeep);
        Directory.CreateDirectory(vm.BackupsFolderPath);
        var before = Path.Combine(vm.BackupsFolderPath, "2026-01-01_000000_before-update.zip");
        File.WriteAllText(before, "");

        for (var i = 0; i < 3; i++)
        {
            await vm.BackUpNowCommand.ExecuteAsync(null);
        }

        Assert.Equal(2, vm.Backups.Count(b => b.Name.Contains("manual")));
        Assert.True(File.Exists(before), "other kinds keep their own count");
    }

    /// <summary>A server added with Settings › Backups to keep › Server backups at 2 prunes to 2 without anyone touching its own box.</summary>
    [Fact]
    public async Task BackUpNow_NewServer_KeepsTheServerBackupsSettingsCount()
    {
        WriteProspect("World A");
        Settings.Current.ServerBackupsKept = 2;
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        for (var i = 0; i < 4; i++)
        {
            await vm.BackUpNowCommand.ExecuteAsync(null);
        }

        Assert.Equal(2, vm.Backups.Count(b => b.Name.Contains("manual")));
    }

    /// <summary>Typing "15" over "10" passes through "1": that used to delete all but one backup of each kind on the keystroke. Now nothing goes until the box is left.</summary>
    [Fact]
    public void BackupsToKeep_IsSavedPerKeystroke_ButPrunesOnlyWhenApplied()
    {
        var (vm, store, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        Directory.CreateDirectory(vm.BackupsFolderPath);
        for (var i = 1; i <= 3; i++)
        {
            var zip = Path.Combine(vm.BackupsFolderPath, $"2026-01-0{i}_000000_manual.zip");
            File.WriteAllText(zip, "");
            File.SetLastWriteTimeUtc(zip, new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));
        }

        vm.BackupsToKeepText = "";
        vm.BackupsToKeepText = "1";
        Assert.Equal(1, store.Profiles.Single().BackupsToKeep);
        vm.BackupsToKeepText = "15";

        Assert.Equal(3, Directory.GetFiles(vm.BackupsFolderPath, "*.zip").Length);
        Assert.Equal(15, store.Profiles.Single().BackupsToKeep);

        vm.BackupsToKeepText = "2";
        vm.ApplyBackupsToKeep();
        Assert.Equal(2, Directory.GetFiles(vm.BackupsFolderPath, "*.zip").Length);
        Assert.False(File.Exists(Path.Combine(vm.BackupsFolderPath, "2026-01-01_000000_manual.zip")));
    }

    [Fact]
    public void SaveAccessLists_RequiresSteam64IdsForAdmins()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.AdminWhitelistText = "76561198851388131\nnot-an-id\n";

        vm.SaveAccessListsCommand.Execute(null);
        Assert.Contains("not-an-id", vm.StatusMessage);
        Assert.False(File.Exists(DedicatedServerLayout.AdminWhitelistPath(_install, null)));

        vm.AdminWhitelistText = "76561198851388131\n";
        vm.SaveAccessListsCommand.Execute(null);
        Assert.Equal("76561198851388131\r\n", File.ReadAllText(DedicatedServerLayout.AdminWhitelistPath(_install, null)));
    }

    /// <summary>Real log lines from an Icarus dedicated server (password replaced): the panel shows who is online and what happened, and the join password never reaches the log view.</summary>
    [Fact]
    public void Log_DrivesPlayersAndEventsAndMasksTheJoinPassword()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var log = DedicatedServerLayout.LogPath(_install, null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, string.Join("\r\n",
            "[2026.08.12-17.31.50:975][102]LogNet: Login request: ?password=hunter2?Name=MK-HATERS userId: Steam:UNKNOWN [0x1100001351D72E3] platform: Steam",
            "[2026.08.12-17.31.51:477][117]LogNet: Join succeeded: MK-HATERS",
            "[2026.08.12-17.32.01:410][337]LogLocalSessionSubsystem: Display: SetActiveProspectInfo - ProspectID: Hard Elysium JULY | ProspectDTKey: OpenWorld_Elysium") + "\r\n");

        vm.Tick();

        Assert.Equal("MK-HATERS", Assert.Single(vm.OnlinePlayers).Name);
        Assert.Equal("Hard Elysium JULY", vm.CurrentProspect);
        Assert.Contains(vm.Events, e => e.EndsWith("MK-HATERS joined"));
        Assert.DoesNotContain(vm.LogLines, l => l.Contains("hunter2"));
        vm.HideNoise = false;
        Assert.Contains(vm.LogLines, l => l.Contains("password=***"));
    }

    /// <summary>The app's real path: the log is read off the UI thread (once, shared with automation) and the panels fill when it is in.</summary>
    [Fact]
    public async Task Log_IsReadOffTheUiThread_AndSharedWithAutomation()
    {
        var log = DedicatedServerLayout.LogPath(_install, null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "[2026.08.12-17.31.51:477][117]LogNet: Join succeeded: MK-HATERS\r\n");
        var (vm, _, processes, _) = Build(pick: _install);
        vm.RunWorkInline = false;

        vm.AddExistingServerCommand.Execute(null);
        await vm.LogLoadTask;

        Assert.Equal("MK-HATERS", Assert.Single(vm.OnlinePlayers).Name);
        Assert.Single(vm.LogLines);

        processes.Running = new RunningServer(5, DateTime.Now);
        await vm.RunAutomationBeatAsync(); // reads through the same watch: no second copy of the line
        File.AppendAllText(log, "[2026.08.12-17.40.00:000][117]LogNet: Join succeeded: Friend\r\n");
        await vm.TickAsync();

        Assert.Equal(2, vm.OnlinePlayers.Count);
        Assert.Equal(2, vm.LogLines.Count);
        Assert.True(vm.IsRunning);
    }

    private void WriteManifest(int buildId, string? betaKey = null)
    {
        var path = DedicatedServerLayout.AppManifestPath(_install);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"\"AppState\"\n{{\n\t\"appid\"\t\t\"2089300\"\n\t\"buildid\"\t\t\"{buildId}\"\n\t\"UserConfig\"\n\t{{\n{(betaKey is null ? "" : $"\t\t\"BetaKey\"\t\t\"{betaKey}\"\n")}\t}}\n}}\n");
    }

    private void WriteProspect(string name, string content = "{}")
    {
        var folder = DedicatedServerLayout.ProspectsFolder(_install, null);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".json"), content);
    }

    /// <summary>A fake game install with UE4SS: Icarus\Binaries\Win64\{dwmapi.dll, ue4ss\UE4SS.dll, ue4ss\Mods\…}. Returns its Win64 folder.</summary>
    /// <summary>A server with a merged pak and its ISL-Merged.txt listing <paramref name="modNames"/>.</summary>
    private void InstallServerMergedPak(params string[] modNames)
    {
        var paks = DedicatedServerLayout.PakModsFolder(_install);
        Directory.CreateDirectory(paks);
        File.WriteAllBytes(Path.Combine(paks, IcarusStarlink.PakIO.InstallManifestNames.MergedPakFileName), [1, 2, 3]);
        File.WriteAllText(Path.Combine(paks, IcarusStarlink.PakIO.InstallManifestNames.PakManifest), "Includes the following mods:\r\n" + string.Join("\r\n", modNames) + "\r\n");
    }

    private static LibraryEntry Entry(string name, int? nexusModId = null) => new()
    {
        FolderName = name, Name = name, Author = "A", Version = "1", Description = "", FileName = name, NexusModId = nexusModId,
    };

    /// <summary>
    /// Stage 3 (2026-10-27): Export for players is a patch of what the server runs — its merged
    /// pak's mod list matched to the Library (a mod the Library lacks is named, not exported), and
    /// the UE4SS mods on on the server with the server's own settings.
    /// </summary>
    [Fact]
    public async Task ExportServerPatch_CarriesTheServersModListAndItsUe4ssModsWithSettings()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var library = new FakeLibrary { Entries = [Entry("Wireless Power", nexusModId: 361)] };
        var patches = new FakePatchService();
        var (vm, _, _, dialogs) = Build(pick: _install, library: library, patches: patches);
        vm.AddExistingServerCommand.Execute(null);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        vm.MatchServerToGameCommand.Execute(null);
        var serverMod = Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "MkHatersTools");
        File.WriteAllText(Path.Combine(serverMod, "config.lua"), "return { Range = 50 }");
        InstallServerMergedPak("Wireless Power", "Something Else");
        dialogs.SaveFilePath = Path.Combine(_root, "ISL-Patch-server.json");

        await vm.ExportServerPatchCommand.ExecuteAsync(null);

        var manifest = patches.ExportedManifest!;
        Assert.Equal(PatchManifest.CurrentFormatVersion, manifest.FormatVersion);
        var mod = Assert.Single(manifest.Mods);
        Assert.Equal(("Wireless Power", 361, false), (mod.Name, mod.NexusModId, mod.Bundled));
        var tools = Assert.Single(manifest.Ue4ssMods);
        Assert.Equal(("MkHatersTools", true), (tools.Name, tools.Bundled)); // no Nexus id known: bundled from the server's folder
        Assert.Equal("return { Range = 50 }", Assert.Single(tools.Configs).Text); // the server's settings, not the game's
        Assert.Equal(serverMod, patches.ExportedBundle!.Ue4ssFolders["MkHatersTools"]);
        Assert.Contains("Not in your Library, so not in the patch: Something Else", vm.ShareStatusMessage);
    }

    /// <summary>Apply a patch: Merge &amp; Install does the Library side, then the patch's UE4SS mods go on the server from the game's copies, with the patch's settings written into the server's copy.</summary>
    [Fact]
    public async Task ApplyPatchToServer_ImportsOnTheLibrarySide_ThenPutsTheUe4ssModsAndSettingsOnTheServer()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var gameMod = Path.Combine(gameWin64, "ue4ss", "Mods", "MkHatersTools");
        File.WriteAllText(Path.Combine(gameMod, "config.lua"), "return {\n    Range = 10, -- how far\n}\n");
        var patches = new FakePatchService
        {
            ImportResult = new PatchImportContents(
                new PatchManifest
                {
                    FormatVersion = 2, ProfileName = "Server",
                    Ue4ssMods = [new PatchUe4ssEntry { Name = "MkHatersTools", Configs = [new PatchConfigFile { Path = "config.lua", Text = "return {\n    Range = 50,\n}\n" }] }],
                },
                new Dictionary<string, IcarusStarlink.PakIO.Container.ExmodPackageContents>()),
        };
        var importer = new FakePatchImporter { Result = "Imported 'Server'." };
        var (vm, _, _, dialogs) = Build(pick: _install, patches: patches, importer: importer);
        vm.AddExistingServerCommand.Execute(null);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        dialogs.OpenFilePath = Path.Combine(_root, "patch.zip");

        await vm.ApplyPatchToServerCommand.ExecuteAsync(null);

        Assert.Equal(1, importer.Calls);
        var serverMod = Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "MkHatersTools");
        Assert.True(File.Exists(Path.Combine(serverMod, "Scripts", "main.lua")));
        // The game copy's layout with the patch's value.
        Assert.Equal("return {\n    Range = 50, -- how far\n}\n", File.ReadAllText(Path.Combine(serverMod, "config.lua")));
        Assert.StartsWith("Applied 'Server'. Imported 'Server'.", vm.ShareStatusMessage);
        Assert.Contains("1 UE4SS mod(s) put on the server (MkHatersTools)", vm.ShareStatusMessage);
        Assert.Contains("build and install the merged pak", vm.ShareStatusMessage);
        Assert.Equal(ServerModState.Same, vm.Ue4ssModRows.Single().State);
    }

    /// <summary>Compare a patch: one row per mod with what the patch, the game and the server have; settings that differ from the patch's are said so.</summary>
    [Fact]
    public async Task ComparePatch_OneRowPerMod_AcrossPatchGameAndServer()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        var content = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        Settings.Current.IcarusContentPath = content;
        File.WriteAllText(Path.Combine(gameWin64, "ue4ss", "Mods", "MkHatersTools", "config.lua"), "return { Range = 10 }");
        Directory.CreateDirectory(Path.Combine(content, "Paks", "mods"));
        File.WriteAllText(Path.Combine(content, "Paks", "mods", IcarusStarlink.PakIO.InstallManifestNames.PakManifest), "Includes the following mods:\r\nWireless Power\r\nOnly In Game\r\n");
        InstallServerMergedPak("Wireless Power");
        var patches = new FakePatchService
        {
            ImportResult = new PatchImportContents(
                new PatchManifest
                {
                    FormatVersion = 2, ProfileName = "Server", ExportedAtUtc = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), StarlinkVersion = "3.4.0",
                    Mods =
                    [
                        new PatchModEntry { FolderName = "W", Name = "Wireless Power", Author = "A", Version = "2", Bundled = false, NexusModId = 361 },
                        new PatchModEntry { FolderName = "N", Name = "Nowhere Yet", Author = "A", Version = "1", Bundled = false },
                    ],
                    Ue4ssMods = [new PatchUe4ssEntry { Name = "MkHatersTools", Version = "1.0", Configs = [new PatchConfigFile { Path = "config.lua", Text = "return { Range = 50 }" }] }],
                },
                new Dictionary<string, IcarusStarlink.PakIO.Container.ExmodPackageContents>()),
        };
        var (vm, _, _, dialogs) = Build(pick: _install, library: new FakeLibrary(), patches: patches);
        vm.AddExistingServerCommand.Execute(null);
        dialogs.OpenFilePath = Path.Combine(_root, "patch.json");

        await vm.ComparePatchCommand.ExecuteAsync(null);

        Assert.True(vm.HasPatchCompare);
        Assert.Contains("'Server' exported", vm.PatchCompareTitle);
        var rows = vm.PatchCompareRows.ToDictionary(r => r.Name);
        Assert.Equal(("v2", "installed", "installed", false), (rows["Wireless Power"].PatchText, rows["Wireless Power"].GameText, rows["Wireless Power"].ServerText, rows["Wireless Power"].Differs));
        Assert.Equal(("v1", "—", "—"), (rows["Nowhere Yet"].PatchText, rows["Nowhere Yet"].GameText, rows["Nowhere Yet"].ServerText));
        Assert.Equal(("—", "installed", "—"), (rows["Only In Game"].PatchText, rows["Only In Game"].GameText, rows["Only In Game"].ServerText));
        Assert.Equal(("on (v1.0), with settings", "on · settings differ", "—"), (rows["MkHatersTools"].PatchText, rows["MkHatersTools"].GameText, rows["MkHatersTools"].ServerText));

        vm.ClosePatchCompareCommand.Execute(null);
        Assert.False(vm.HasPatchCompare);
    }

    /// <summary>Review 2026-10-27: Merge &amp; Install refusing the import (a build in progress) used to read as "no importer" and the UE4SS mods still went on the server.</summary>
    [Fact]
    public async Task ApplyPatchToServer_WhenMergeInstallIsBusy_PutsNothingOnTheServer()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var patches = new FakePatchService
        {
            ImportResult = new PatchImportContents(
                new PatchManifest { FormatVersion = 2, ProfileName = "Server", Ue4ssMods = [new PatchUe4ssEntry { Name = "MkHatersTools" }] },
                new Dictionary<string, IcarusStarlink.PakIO.Container.ExmodPackageContents>()),
        };
        var importer = new FakePatchImporter { Result = null };
        var (vm, _, _, dialogs) = Build(pick: _install, patches: patches, importer: importer);
        vm.AddExistingServerCommand.Execute(null);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        dialogs.OpenFilePath = Path.Combine(_root, "patch.zip");

        await vm.ApplyPatchToServerCommand.ExecuteAsync(null);

        Assert.Contains("busy", vm.ShareStatusMessage);
        Assert.False(Directory.Exists(Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "MkHatersTools")));
    }

    /// <summary>Review 2026-10-27: a pak mod is its own pak file in Paks\mods, never a name in ISL-Merged.txt, so it read as missing everywhere.</summary>
    [Fact]
    public async Task ComparePatch_PakMod_IsFoundByItsPakFiles()
    {
        var content = Path.Combine(_root, "Game", "Icarus", "Content");
        Settings.Current.IcarusContentPath = content;
        Directory.CreateDirectory(Path.Combine(content, "Paks", "mods"));
        File.WriteAllBytes(Path.Combine(content, "Paks", "mods", "Model_P.pak"), [1]);
        var patches = new FakePatchService
        {
            ImportResult = new PatchImportContents(
                new PatchManifest
                {
                    FormatVersion = 2, ProfileName = "S",
                    Mods = [new PatchModEntry { FolderName = "M", Name = "My Model", Author = "A", Version = "1", Bundled = false, NexusModId = 1, IsPak = true, PakFileNames = ["Model_P.pak"] }],
                },
                new Dictionary<string, IcarusStarlink.PakIO.Container.ExmodPackageContents>()),
        };
        var (vm, _, _, dialogs) = Build(pick: _install, library: new FakeLibrary(), patches: patches);
        vm.AddExistingServerCommand.Execute(null);
        dialogs.OpenFilePath = Path.Combine(_root, "patch.json");

        await vm.ComparePatchCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.PatchCompareRows);
        Assert.Equal(("Pak mod", "installed", "—"), (row.Kind, row.GameText, row.ServerText));
    }

    private string MakeGameWithUe4ss(params (string Name, bool Enabled)[] mods)
    {
        var win64 = Path.Combine(_root, "Game", "Icarus", "Binaries", "Win64");
        var modsFolder = Path.Combine(win64, "ue4ss", "Mods");
        Directory.CreateDirectory(Path.Combine(modsFolder, "shared"));
        Directory.CreateDirectory(Path.Combine(modsFolder, "Keybinds"));
        File.WriteAllText(Path.Combine(win64, "dwmapi.dll"), "proxy");
        File.WriteAllText(Path.Combine(win64, "ue4ss", "UE4SS.dll"), "loader");
        File.WriteAllText(Path.Combine(win64, "ue4ss", "UE4SS-settings.ini"), "[General]\n");
        var txt = new List<string>();
        foreach (var (name, enabled) in mods)
        {
            Directory.CreateDirectory(Path.Combine(modsFolder, name, "Scripts"));
            File.WriteAllText(Path.Combine(modsFolder, name, "Scripts", "main.lua"), "-- " + name);
            txt.Add($"{name} : {(enabled ? 1 : 0)}");
        }

        txt.AddRange(["", "; Built-in keybinds, do not move up!", "Keybinds : 1"]);
        File.WriteAllText(Path.Combine(modsFolder, "mods.txt"), string.Join("\r\n", txt) + "\r\n");
        return win64;
    }

    private sealed class FakeSteamCmd : ISteamCmdService
    {
        public List<(string Folder, string Branch)> Installs { get; } = [];
        public bool CreateServerOnInstall { get; set; }
        public string? FailWith { get; set; }
        public int? LatestBuild { get; set; }

        /// <summary>Starlink's own copy (null = not installed yet); Chosen mirrors AppSettings.SteamCmdExePath like the real service.</summary>
        public string? OwnExe { get; set; } = @"C:\fake\steamcmd.exe";
        public Func<string?>? Chosen { get; set; }
        public string? CurrentExePath => Chosen?.Invoke() is { } chosen && File.Exists(chosen) ? chosen : OwnExe;
        public string OwnInstallFolder => @"C:\Starlink\SteamCMD";
        public int OwnInstalls { get; private set; }

        public async Task InstallOwnCopyAsync(Action<string> onLine, CancellationToken cancellationToken = default)
        {
            OwnInstalls++;
            if (WaitForCancel)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            OwnExe = OwnInstallFolder + @"\steamcmd.exe";
        }

        /// <summary>Runs while SteamCMD "downloads" (for "what can the user do during an update").</summary>
        public Action? DuringInstall { get; set; }

        /// <summary>Makes the next install run until it is cancelled, like a long download.</summary>
        public bool WaitForCancel { get; set; }

        public async Task<SteamCmdResult> InstallOrUpdateAsync(string installFolder, string branch, Action<string> onLine, CancellationToken cancellationToken = default)
        {
            Installs.Add((installFolder, branch));
            DuringInstall?.Invoke();
            onLine(" Update state (0x61) downloading, progress: 50.00 (1 / 2)");
            if (WaitForCancel)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Finish(installFolder, onLine);
        }

        private SteamCmdResult Finish(string installFolder, Action<string> onLine)
        {
            if (FailWith is not null)
            {
                onLine(FailWith);
                return new SteamCmdResult(false, FailWith);
            }

            if (CreateServerOnInstall)
            {
                Directory.CreateDirectory(installFolder);
                File.WriteAllText(Path.Combine(installFolder, "IcarusServer.exe"), "");
            }

            return new SteamCmdResult(true, "ok");
        }

        public Task<int?> GetLatestBuildIdAsync(string branch, CancellationToken cancellationToken = default) => Task.FromResult(LatestBuild);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public bool Save() => true;
    }

    private sealed class FakeStore : IDedicatedServerProfileStore
    {
        public List<DedicatedServerProfile> Profiles { get; } = [];
        public IReadOnlyList<DedicatedServerProfile> GetAll() => Profiles;

        public string? LoadProblem { get; set; }

        public bool Save(DedicatedServerProfile profile)
        {
            Profiles.RemoveAll(p => p.Id == profile.Id);
            Profiles.Add(profile);
            return LoadProblem is null;
        }

        public bool Delete(Guid id)
        {
            Profiles.RemoveAll(p => p.Id == id);
            return LoadProblem is null;
        }
    }

    private sealed class FakeProcesses : IDedicatedServerProcessService
    {
        public RunningServer? Running { get; set; }
        public bool RunAfterStart { get; set; }
        public bool StopIsClean { get; set; } = true;
        public List<DedicatedServerProfile> Started { get; } = [];
        public List<int> Stopped { get; } = [];

        /// <summary>Set to change what FindRunning answers from then on (for "it changed between two looks").</summary>
        public Func<RunningServer?>? FindRunningHook { get; set; }

        public int FindRunningCalls { get; private set; }

        /// <summary>Set to answer per profile (several servers, some running); otherwise every profile gets Running.</summary>
        public Func<DedicatedServerProfile, RunningServer?>? RunningFor { get; set; }

        public RunningServer? FindRunning(DedicatedServerProfile profile)
        {
            FindRunningCalls++;
            return FindRunningHook is { } hook ? hook() : RunningFor is { } perProfile ? perProfile(profile) : Running;
        }

        /// <summary>Runs as a server is launched (for "what the page shows while automation restarts it").</summary>
        public Action? DuringStart { get; set; }

        public void Start(DedicatedServerProfile profile)
        {
            DuringStart?.Invoke();
            Started.Add(profile);
            if (RunAfterStart)
            {
                Running = new RunningServer(42, DateTime.Now);
            }
        }

        /// <summary>Runs while a stop is in progress (for "the user did something during the 60-second wait").</summary>
        public Action? DuringStop { get; set; }

        public Task<bool> StopAsync(RunningServer server, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            DuringStop?.Invoke();
            Stopped.Add(server.ProcessId);
            Running = null;
            return Task.FromResult(StopIsClean);
        }

        public long? GetMemoryBytes(int processId) => Running?.ProcessId == processId ? 8L << 30 : null;

        public List<int> Minimized { get; } = [];

        public bool TryMinimizeConsole(int processId)
        {
            Minimized.Add(processId);
            return true;
        }
    }

    private sealed class FakeDialogs(bool confirmResult) : IDialogService
    {
        public List<string> Messages { get; } = [];

        /// <summary>Answers for the next confirms, in order; once empty, every confirm gets confirmResult.</summary>
        public Queue<bool> Answers { get; } = new();

        /// <summary>Runs while a question is open (something changing on disk before the answer).</summary>
        public Action? WhileAsking { get; set; }

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            Messages.Add(message);
            WhileAsking?.Invoke();
            return Answers.Count > 0 ? Answers.Dequeue() : confirmResult;
        }

        /// <summary>The answer to a three-way question (true = the first button, false = the second, null = cancel).</summary>
        public bool? ThreeWayAnswer { get; set; }

        public bool? ConfirmWithCancel(string message, string title, ThemedConfirmSeverity severity, string yesText, string noText)
        {
            Messages.Add(message);
            return ThreeWayAnswer;
        }

        public RenamePromptResult PromptRename(string currentName, string description = "", string? resetValue = null, string resetLabel = "", string resetTooltip = "", string title = "", string fieldLabel = "") =>
            throw new NotSupportedException();

        /// <summary>What a file picker returns (Apply a patch, Compare a patch); null cancels.</summary>
        public string? OpenFilePath { get; set; }

        /// <summary>Where a save picker saves (Export patch for players); null cancels.</summary>
        public string? SaveFilePath { get; set; }

        public string? PickFile(string title, string filter, string? initialDirectory = null) => OpenFilePath;

        public string? PickSaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null) => SaveFilePath;
    }

    /// <summary>What Export for players needs of the Library: the entries, and a folder for a bundled mod (none here: every test mod has a source).</summary>
    private sealed class FakeLibrary : ILibraryRepository
    {
        public List<LibraryEntry> Entries { get; set; } = [];

        public IReadOnlyList<LibraryEntry> GetAll() => Entries;
        public string GetFolderPath(string folderName) => Path.Combine(Path.GetTempPath(), "nowhere", folderName);
        public IReadOnlyList<string> UnreadableFolders => [];
        public IReadOnlyList<LibraryEntry> Search(string query) => throw new NotSupportedException();
        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null) => throw new NotSupportedException();
        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null) => throw new NotSupportedException();
        public LibraryEntry ImportPaks(IReadOnlyList<string> pakFilePaths, string name, string? source = null, int? nexusModId = null, string? catalogEntryId = null) => throw new NotSupportedException();
        public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version) => throw new NotSupportedException();
        public void Refresh() => throw new NotSupportedException();
        public void Delete(string folderName) => throw new NotSupportedException();
        public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes) => throw new NotSupportedException();
        public void MarkLocallyEdited(string folderName) => throw new NotSupportedException();
        public void MarkConvertedFromPrebuiltPak(string folderName) => throw new NotSupportedException();
        public void SetDisplayNameOverride(string folderName, string? displayName) => throw new NotSupportedException();
        public void LinkToNexus(string folderName, int nexusModId) => throw new NotSupportedException();
        public void SetCatalogEntry(string folderName, string catalogEntryId) => throw new NotSupportedException();
        public string BackupMod(string folderName) => throw new NotSupportedException();
        public bool HasModBackup(string folderName) => false;
        public bool RestoreLatestModBackup(string folderName) => throw new NotSupportedException();
        public string? TryGetLatestModBackupPath(string folderName) => null;
        public LibraryEntry CreateBlankMod(string name, string author, ModTemplate template = ModTemplate.Blank) => throw new NotSupportedException();
        public IReadOnlyList<string> ListAssetPaths(string folderName) => [];
        public IReadOnlyList<string> ListAssetPaths(string folderName, IReadOnlyList<string> precomputedFiles) => [];
        public byte[] ReadAssetContent(string folderName, string relativePath) => throw new NotSupportedException();
        public string? ReadReadme(string folderName) => null;
        public string? ReadReadme(string folderName, IReadOnlyList<string> precomputedFiles) => null;
        public IReadOnlyList<string> ListFolderFiles(string folderName) => [];
    }

    private sealed class FakePatchService : IPatchService
    {
        public PatchManifest? ExportedManifest { get; private set; }
        public PatchBundle? ExportedBundle { get; private set; }
        public PatchImportContents? ImportResult { get; set; }

        public Task ExportAsync(PatchManifest manifest, PatchBundle bundle, string outputPath, CancellationToken cancellationToken = default)
        {
            ExportedManifest = manifest;
            ExportedBundle = bundle;
            return Task.CompletedTask;
        }

        public Task<PatchImportContents> ImportAsync(string patchFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImportResult ?? throw new NotSupportedException("Set ImportResult first."));
    }

    /// <summary>Stands in for Merge &amp; Install's Library-side import.</summary>
    private sealed class FakePatchImporter : IPatchImporter
    {
        public string? Result { get; set; }
        public int Calls { get; private set; }

        public Task<string?> ImportPatchContentsAsync(PatchImportContents contents)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
