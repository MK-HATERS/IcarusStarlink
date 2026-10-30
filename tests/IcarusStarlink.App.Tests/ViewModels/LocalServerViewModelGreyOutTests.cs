using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// User request 2026-10-28: the Mods, Backups, Share, World settings and admin/ban list buttons
/// grey out (with a tooltip saying why) while they can't run, instead of refusing after the click.
/// The guards inside the commands stay; these tests are about CanExecute and the reason text.
/// </summary>
public sealed partial class LocalServerViewModelTests
{
    private static readonly ServerUe4ssRow TestRow = new(new ServerUe4ssComparison(
        "MkHatersTools", true, true, "1.0", true, true, "1.0", ServerModState.Same, []));

    /// <summary>Every command that changes the server's files, with what it needs selected; true = all of them could run.</summary>
    private static Dictionary<string, bool> GuardedCanExecute(LocalServerViewModel vm) => new()
    {
        ["Install my merged pak"] = vm.InstallMergedPakCommand.CanExecute(null),
        ["Remove selected pak"] = vm.RemoveSelectedPakCommand.CanExecute(null),
        ["Install UE4SS from my game"] = vm.InstallUe4ssOnServerCommand.CanExecute(null),
        ["Copy a UE4SS mod"] = vm.CopyUe4ssModToServerCommand.CanExecute(TestRow),
        ["Make the server match my game"] = vm.MatchServerToGameCommand.CanExecute(null),
        ["Remove a UE4SS mod"] = vm.RemoveServerUe4ssModCommand.CanExecute(TestRow),
        ["Restore selected"] = vm.RestoreSelectedBackupCommand.CanExecute(null),
        ["Save lists"] = vm.SaveAccessListsCommand.CanExecute(null),
        ["Apply a patch"] = vm.ApplyPatchToServerCommand.CanExecute(null),
        ["Save world settings"] = vm.WorldSettings.SaveCommand.CanExecute(null),
    };

    private static void AssertAll(LocalServerViewModel vm, bool expected)
    {
        var wrong = GuardedCanExecute(vm).Where(kv => kv.Value != expected).Select(kv => kv.Key).ToList();
        Assert.True(wrong.Count == 0, $"{(expected ? "Greyed out" : "Still clickable")}: {string.Join(", ", wrong)}");
    }

    private (LocalServerViewModel Vm, FakeProcesses Processes) BuildForGreyOut()
    {
        var (vm, _, processes, _) = BuildWithServer();
        vm.SelectedServerPak = new ServerPakFile("a_P.pak", 1, DateTime.Now);
        vm.SelectedBackup = new ServerBackupInfo(Path.Combine(_root, "b.zip"), "b", DateTime.Now, 1);
        return (vm, processes);
    }

    [Fact]
    public async Task ServerFileButtons_GreyOutWhileTheServerRuns_AndComeBackWhenItStops()
    {
        var (vm, processes) = BuildForGreyOut();
        AssertAll(vm, true);
        Assert.Null(vm.ServerFilesBlockedReason);
        Assert.Null(vm.InstallFilesBlockedReason);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();

        AssertAll(vm, false);
        Assert.Equal("Stop the server first.", vm.ServerFilesBlockedReason);
        Assert.Equal("Stop the server first.", vm.InstallFilesBlockedReason);
        Assert.Contains("Stop the server first", vm.WorldSettings.SaveBlockedReason);
        Assert.Contains(nameof(LocalServerViewModel.InstallFilesBlockedReason), raised); // the tooltips follow
        Assert.True(vm.BackUpNowCommand.CanExecute(null), "Back up now stays allowed while the server runs");

        processes.Running = null;
        await vm.TickAsync();

        AssertAll(vm, true);
        Assert.Null(vm.ServerFilesBlockedReason);
        Assert.Null(vm.WorldSettings.SaveBlockedReason);
    }

    [Fact]
    public void ServerFileButtons_GreyOutWhileStarting_AndWhileABackupWrites()
    {
        var (vm, _) = BuildForGreyOut();

        vm.IsBusy = true;
        AssertAll(vm, false);
        Assert.Equal("Waiting for the server to finish starting.", vm.InstallFilesBlockedReason);

        vm.IsBusy = false;
        AssertAll(vm, true);

        vm.IsBackupBusy = true;
        Assert.Equal("Waiting for the backup or mod copy to finish.", vm.ServerFilesBlockedReason);
        Assert.False(vm.InstallMergedPakCommand.CanExecute(null));
        vm.IsBackupBusy = false;
        AssertAll(vm, true);
    }

    [Fact]
    public async Task ServerFileButtons_GreyOutWhileUpdating_WithTheUpdateReason()
    {
        var (vm, _) = BuildForGreyOut();
        Dictionary<string, bool>? during = null;
        string? reason = null;
        SteamCmd.DuringInstall = () =>
        {
            during = GuardedCanExecute(vm);
            reason = vm.InstallFilesBlockedReason;
        };

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.NotNull(during);
        Assert.All(during!, kv => Assert.False(kv.Value, kv.Key));
        Assert.Equal("Waiting for the update to finish.", reason);
        AssertAll(vm, true);
    }

    [Fact]
    public async Task ServerFileButtons_GreyOutWhileAutomationRestartsTheServer()
    {
        var (vm, processes) = BuildForGreyOut();
        WriteLog("[2026.08.12-18.05.54:868][  1]LogInit: running");
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.RunAutomationBeatAsync();
        Dictionary<string, bool>? during = null;
        string? reason = null;
        processes.DuringStart = () =>
        {
            during = GuardedCanExecute(vm);
            reason = vm.ServerFilesBlockedReason;
        };

        await SeeItStopAsync(vm, processes); // a crash: automation starts it again

        Assert.NotNull(during);
        Assert.All(during!, kv => Assert.False(kv.Value, kv.Key));
        Assert.Equal("Waiting for the automatic restart to finish.", reason);
        processes.Running = null;
        await vm.TickAsync();
        AssertAll(vm, true);
    }

    /// <summary>Another profile on the same install running: the install's Mods buttons grey out naming it; this server's own lists and restore don't.</summary>
    [Fact]
    public async Task InstallButtons_GreyOutWhileAnotherProfileOnTheInstallRuns()
    {
        var (vm, processes, _, sibling) = BuildWithSibling();
        vm.SelectedBackup = new ServerBackupInfo(Path.Combine(_root, "b.zip"), "b", DateTime.Now, 1);
        await vm.RunAutomationBeatAsync();

        Assert.Equal("Stop 'Second world' first: it runs from the same install.", vm.InstallFilesBlockedReason);
        Assert.False(vm.CanChangeInstallFiles);
        Assert.False(vm.InstallMergedPakCommand.CanExecute(null));
        Assert.False(vm.ApplyPatchToServerCommand.CanExecute(null));
        Assert.False(vm.CopyUe4ssModToServerCommand.CanExecute(TestRow));
        Assert.True(vm.CanChangeServerFiles);
        Assert.True(vm.SaveAccessListsCommand.CanExecute(null));
        Assert.True(vm.RestoreSelectedBackupCommand.CanExecute(null));

        processes.RunningFor = _ => null;
        await vm.RunAutomationBeatAsync();
        await vm.RunAutomationBeatAsync(); // gone for two beats before it counts as stopped

        Assert.Null(vm.InstallFilesBlockedReason);
        Assert.True(vm.InstallMergedPakCommand.CanExecute(null));
        _ = sibling;
    }

    /// <summary>The guard stays the last line of defence: a click that got through as the server started is still refused.</summary>
    [Fact]
    public async Task SaveLists_RunAnyway_WhileRunning_IsStillRefused()
    {
        var (vm, processes) = BuildForGreyOut();
        processes.Running = new RunningServer(9, DateTime.Now);
        await vm.TickAsync();

        vm.SaveAccessListsCommand.Execute(null);

        Assert.Contains("Stop the server before you edit the admin and ban lists", vm.StatusMessage);
    }

    // ---- ServerSettings.ini changed on disk ----

    private const string IniHeader = "[/Script/Icarus.DedicatedServerSettings]\r\n";

    /// <summary>A server whose ini says MaxPlayers=6 and AdminPassword=old, loaded into the form.</summary>
    private (LocalServerViewModel Vm, FakeProcesses Processes, FakeDialogs Dialogs) BuildWithIni()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, IniHeader + "MaxPlayers=6\r\nAdminPassword=old\r\n");
        var (vm, _, processes, dialogs) = BuildWithServer();
        Assert.Equal("6", vm.MaxPlayersText);
        return (vm, processes, dialogs);
    }

    /// <summary>The owner edits the file by hand after the page read it.</summary>
    private void HandEditIni(string body)
    {
        File.WriteAllText(IniPath, IniHeader + body);
        File.SetLastWriteTimeUtc(IniPath, DateTime.UtcNow.AddMinutes(1));
    }

    private ServerSettingsIni IniOnDisk() => ServerSettingsIni.Load(IniPath);

    [Fact]
    public void Save_IniChangedOnDisk_CleanForm_TakesTheFileSilently()
    {
        var (vm, _, dialogs) = BuildWithIni();
        HandEditIni("MaxPlayers=8\r\nAdminPassword=new\r\n");

        vm.SaveSettingsCommand.Execute(null);

        Assert.Empty(dialogs.Messages);
        Assert.Equal("8", vm.MaxPlayersText);
        var onDisk = IniOnDisk().Read();
        Assert.Equal(8, onDisk.MaxPlayers);
        Assert.Equal("new", onDisk.AdminPassword);
        Assert.Contains("changed outside Starlink", vm.StatusMessage);
    }

    [Fact]
    public void Save_ServerRewroteIniWithOnlyItsOwnKeys_DirtyForm_SavesWithoutAsking()
    {
        // The server rewrites the whole file as it runs: a new LastProspectName and its own number
        // format ("6" → "6.000000"). None of that is a setting the form edits, so no question.
        var (vm, _, dialogs) = BuildWithIni();
        vm.ResumeProspect = false;
        HandEditIni("MaxPlayers=6.000000\r\nAdminPassword=old\r\nLastProspectName=Test Olympus\r\n");

        vm.SaveSettingsCommand.Execute(null);

        Assert.Empty(dialogs.Messages);
        var onDisk = IniOnDisk().Read();
        Assert.False(onDisk.ResumeProspect);
        Assert.Equal("Test Olympus", onDisk.LastProspectName);
        Assert.Equal("Test Olympus", vm.LastProspectName);
    }

    [Fact]
    public void ResumeProspect_UnsetInTheFile_ShowsOn_AndIsSavedAsTrueOrFalse()
    {
        // A blank value fails the game's own import ("import failed for ResumeProspect"), so the
        // server falls back to resuming while the three-state box looked off.
        var (vm, _, _) = BuildWithIni();
        Assert.True(vm.ResumeProspect);

        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal("True", IniOnDisk().GetRaw("ResumeProspect"));

        vm.ResumeProspect = false;
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal("False", IniOnDisk().GetRaw("ResumeProspect"));
    }

    [Fact]
    public void Save_IniUnchanged_DirtyForm_SavesWithoutAsking()
    {
        var (vm, _, dialogs) = BuildWithIni();
        vm.MaxPlayersText = "4";

        vm.SaveSettingsCommand.Execute(null);

        Assert.Empty(dialogs.Messages);
        Assert.Equal(4, IniOnDisk().Read().MaxPlayers);
    }

    [Fact]
    public void Save_IniChangedOnDisk_DirtyForm_Reload_TakesTheFileAndDropsTheEdits()
    {
        var (vm, _, dialogs) = BuildWithIni();
        vm.MaxPlayersText = "4";
        HandEditIni("MaxPlayers=8\r\nAdminPassword=new\r\n");
        dialogs.ThreeWayAnswer = true;

        vm.SaveSettingsCommand.Execute(null);

        Assert.Contains("ServerSettings.ini was changed outside Starlink", Assert.Single(dialogs.Messages));
        Assert.Equal("8", vm.MaxPlayersText);
        Assert.Equal("new", vm.AdminPassword);
        var onDisk = IniOnDisk().Read();
        Assert.Equal(8, onDisk.MaxPlayers);
        Assert.Equal("new", onDisk.AdminPassword);
    }

    [Fact]
    public void Save_IniChangedOnDisk_DirtyForm_KeepMine_WritesTheFormOverIt()
    {
        var (vm, _, dialogs) = BuildWithIni();
        vm.MaxPlayersText = "4";
        HandEditIni("MaxPlayers=8\r\nAdminPassword=new\r\n");
        dialogs.ThreeWayAnswer = false;

        vm.SaveSettingsCommand.Execute(null);

        Assert.Single(dialogs.Messages);
        var onDisk = IniOnDisk().Read();
        Assert.Equal(4, onDisk.MaxPlayers);
        Assert.Equal("old", onDisk.AdminPassword);

        // Written now, so the next Save doesn't ask again.
        vm.SaveSettingsCommand.Execute(null);
        Assert.Single(dialogs.Messages);
    }

    [Fact]
    public void Save_IniChangedOnDisk_DirtyForm_Cancel_WritesNothing()
    {
        var (vm, _, dialogs) = BuildWithIni();
        vm.MaxPlayersText = "4";
        HandEditIni("MaxPlayers=8\r\nAdminPassword=new\r\n");
        var handEdited = File.ReadAllText(IniPath);
        dialogs.ThreeWayAnswer = null;

        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal(handEdited, File.ReadAllText(IniPath));
        Assert.Equal("4", vm.MaxPlayersText);
        Assert.Contains("Nothing was saved", vm.ValidationMessage);
    }

    [Fact]
    public async Task Start_GoesThroughTheSameCheck()
    {
        var (vm, processes, dialogs) = BuildWithIni();
        processes.RunAfterStart = false;
        vm.MaxPlayersText = "4";
        HandEditIni("MaxPlayers=8\r\nAdminPassword=new\r\n");
        dialogs.ThreeWayAnswer = null;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(processes.Started);
        Assert.Contains("ServerSettings.ini was changed outside Starlink", Assert.Single(dialogs.Messages));
        Assert.Equal(8, IniOnDisk().Read().MaxPlayers);
    }

    [Fact]
    public async Task Start_IniChangedOnDisk_CleanForm_StartsWithTheFile()
    {
        var (vm, processes, dialogs) = BuildWithIni();
        processes.RunAfterStart = false;
        HandEditIni("MaxPlayers=8\r\nAdminPassword=new\r\n");

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Messages);
        Assert.Single(processes.Started);
        Assert.Equal(8, IniOnDisk().Read().MaxPlayers);
        Assert.Equal("new", IniOnDisk().Read().AdminPassword);
    }

    [Fact]
    public void PageShown_IniChangedOnDisk_ReloadsACleanForm_AndLeavesADirtyOneAlone()
    {
        var (vm, _, dialogs) = BuildWithIni();
        HandEditIni("MaxPlayers=8\r\nAdminPassword=new\r\n");

        vm.OnPageShown();

        Assert.Equal("8", vm.MaxPlayersText);
        Assert.Equal("new", vm.AdminPassword);

        vm.MaxPlayersText = "4";
        HandEditIni("MaxPlayers=12\r\nAdminPassword=newer\r\n");
        vm.OnPageShown();

        Assert.Equal("4", vm.MaxPlayersText);
        Assert.Equal("new", vm.AdminPassword);
        Assert.Empty(dialogs.Messages); // the page being shown never asks; Save does
    }
}
