using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Review 2026-10-23: acting on the right server. Two profiles can share one install (each with its
/// own Saved suffix), a restart's update takes minutes, and the page's Running can be another
/// server's or a few seconds old — none of that may make Start, Stop, Update or a mod change act on
/// the wrong server or process.
/// </summary>
public sealed partial class LocalServerViewModelTests
{
    /// <summary>The server added from _install, plus a second profile on the same install (its own Saved suffix).</summary>
    private (LocalServerViewModel Vm, FakeProcesses Processes, DedicatedServerProfile First, DedicatedServerProfile Sibling) BuildWithSibling()
    {
        var (vm, store, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var first = vm.SelectedProfile!;
        var sibling = new DedicatedServerProfile { Name = "Second world", InstallFolder = _install, SavedDirSuffix = "_2", Port = 17778, QueryPort = 27016 };
        store.Save(sibling);
        vm.Profiles.Add(sibling);
        processes.RunningFor = p => p.Id == sibling.Id ? new RunningServer(77, DateTime.Now) : null;
        vm.SelectedProfile = first;
        return (vm, processes, first, sibling);
    }

    [Fact]
    public void RunningSiblings_FindsEveryRunningProfileOnTheSameInstall()
    {
        var (vm, _, first, sibling) = BuildWithSibling();
        var elsewhere = new DedicatedServerProfile { Name = "Elsewhere", InstallFolder = Path.Combine(_root, "Other") };
        vm.Profiles.Add(elsewhere);

        Assert.Equal([sibling.Id], vm.RunningSiblings(first).Select(p => p.Id));
    }

    /// <summary>The paks folder is the install's: with the other profile running, its files would be swapped under a live world.</summary>
    [Fact]
    public async Task InstallMergedPak_AnotherProfileOnTheInstallRunning_IsRefused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
        File.WriteAllText(MergedPakPath, "pak");
        var (vm, _, _, _) = BuildWithSibling();
        vm.RefreshMergedPak();

        await vm.InstallMergedPakCommand.ExecuteAsync(null);

        Assert.Contains("Stop 'Second world'", vm.StatusMessage);
        Assert.False(File.Exists(Path.Combine(DedicatedServerLayout.PakModsFolder(_install), Path.GetFileName(MergedPakPath))));
    }

    [Fact]
    public async Task UpdateServer_AnotherProfileOnTheInstallRunning_IsRefused()
    {
        var (vm, _, _, _) = BuildWithSibling();

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Empty(SteamCmd.Installs);
        Assert.Contains("Stop 'Second world'", vm.StatusMessage);
    }

    /// <summary>Automation's update is skipped before anything is stopped (it used to stop the server, skip, and start it again every beat).</summary>
    [Fact]
    public async Task AutoUpdate_AnotherProfileOnTheInstallRunning_SkipsWithoutStopping()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, processes, first, sibling) = BuildWithSibling();
        vm.AutoUpdate = true;
        vm.Clock = () => DateTime.Today.AddHours(12);
        processes.RunningFor = p => new RunningServer(p.Id == sibling.Id ? 77 : 9, DateTime.Now);

        await vm.RunAutomationBeatAsync(); // the hourly check finds the newer build
        await vm.RunAutomationBeatAsync(); // nobody online: it would update now

        Assert.Empty(processes.Stopped);
        Assert.Empty(SteamCmd.Installs);
        Assert.Contains(vm.RecentEvents, l => l.Contains("skipped the update"));
        Assert.Equal(first.Id, vm.SelectedProfile!.Id);
    }

    /// <summary>The update on restart takes minutes: the server list stays locked, and the server started at the end is the one restarted.</summary>
    [Fact]
    public async Task Restart_WithUpdateOnRestart_KeepsTheListLockedAndStartsTheSameServer()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        var (vm, _, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var first = vm.SelectedProfile!;
        vm.UpdateOnRestart = true;
        processes.Running = new RunningServer(9, DateTime.Now);
        vm.Tick();
        bool? listLockedDuringUpdate = null;
        string? stateDuringUpdate = null;
        SteamCmd.DuringInstall = () =>
        {
            listLockedDuringUpdate = !vm.IsNotBusy;
            stateDuringUpdate = vm.RunStateText;
        };

        await vm.RestartCommand.ExecuteAsync(null);

        Assert.True(listLockedDuringUpdate);
        Assert.Equal("UPDATING", stateDuringUpdate);
        Assert.Equal([9], processes.Stopped);
        Assert.Equal(first.Id, Assert.Single(processes.Started).Id);
    }

    /// <summary>Start used to stay clickable in the middle of a restore.</summary>
    [Fact]
    public void Start_IsGreyedOutWhileABackupOrRestoreRuns()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var seen = new List<bool>();
        vm.StartCommand.CanExecuteChanged += (_, _) => seen.Add(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StartCommand.CanExecute(null));

        vm.IsBackupBusy = true;

        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains(false, seen);
    }

    /// <summary>Selecting a server used to leave the previous server's Running in place until the next process scan.</summary>
    [Fact]
    public async Task SelectingAServer_ShowsItsOwnRunState_RightAway()
    {
        var (vm, store, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var first = vm.SelectedProfile!;
        var other = new DedicatedServerProfile { Name = "Other", InstallFolder = Path.Combine(_root, "Other") };
        store.Save(other);
        vm.Profiles.Add(other);
        processes.RunningFor = p => p.Id == first.Id ? new RunningServer(9, DateTime.Now) : null;
        await vm.RunAutomationBeatAsync();
        await vm.TickAsync();
        Assert.True(vm.IsRunning);

        // The next process scan fails: only what is known about each server can answer.
        processes.FindRunningHook = () => throw new InvalidOperationException("scan failed");
        vm.SelectedProfile = other;

        Assert.False(vm.IsRunning);
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    /// <summary>Stop looks again before acting: a server that exited since the page last looked isn't "stopped" by PID.</summary>
    [Fact]
    public async Task Stop_ServerGoneSinceLastLook_StopsNothing()
    {
        var (vm, _, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(9, DateTime.Now);
        vm.Tick();
        Assert.True(vm.StopCommand.CanExecute(null));
        processes.Running = null;

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Empty(processes.Stopped);
        Assert.Contains("isn't running any more", vm.StatusMessage);
        Assert.False(vm.IsRunning);
    }

    /// <summary>A recorded PID is only acted on while it is still that server: same exe name, same start time.</summary>
    [Theory]
    [InlineData("IcarusServer-Win64-Shipping", 0, true)]
    [InlineData("IcarusServer", 0.4, true)]
    [InlineData("IcarusServer-Win64-Shipping", 30, false)]
    [InlineData("notepad", 0, false)]
    public void IsRecordedServer_NeedsTheSameNameAndStartTime(string name, double secondsLater, bool expected)
    {
        var started = new DateTime(2026, 10, 23, 5, 0, 0);
        Assert.Equal(expected, DedicatedServerProcessService.IsRecordedServer(name, started.AddSeconds(secondsLater), new RunningServer(9, started)));
    }

    /// <summary>A merged pak under its earlier name is moved into the server's backups, not deleted.</summary>
    [Fact]
    public async Task InstallMergedPak_MovesALegacyNamedPakIntoTheBackups()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
        File.WriteAllText(MergedPakPath, "new pak");
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var mods = DedicatedServerLayout.PakModsFolder(_install);
        Directory.CreateDirectory(mods);
        // Installed while ArcFission Library was on; it's off on this server now, so the plain name.
        File.WriteAllText(Path.Combine(mods, "zz_zz_ISL-Merged_9999_P.pak"), "old pak");
        vm.RefreshMergedPak();

        await vm.InstallMergedPakCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(mods, "ISL-Merged_P.pak")));
        Assert.False(File.Exists(Path.Combine(mods, "zz_zz_ISL-Merged_9999_P.pak")));
        var moved = Assert.Single(Directory.GetFiles(Path.Combine(vm.BackupsFolderPath, "removed-paks")));
        Assert.Equal("old pak", File.ReadAllText(moved));
    }
}
