using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Review 2026-10-28 (running a server): settings and lists overwritten from a stale or blank form,
/// and server files changed in the gap between an update's stop and its start.
/// </summary>
public sealed partial class LocalServerViewModelTests
{
    private string MergedPakOnServer => Path.Combine(DedicatedServerLayout.PakModsFolder(_install), Path.GetFileName(MergedPakPath));

    private void WriteMergedPak()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
        File.WriteAllText(MergedPakPath, "pak");
    }

    /// <summary>
    /// An automated update stops the server, runs SteamCMD for minutes, then starts it: Running is
    /// null in between, so a mod copy clicked then went into a server that automation was about to
    /// start (or a restore was written while it booted).
    /// </summary>
    [Fact]
    public async Task AutoUpdate_ModChangeWhileItUpdates_IsRefused()
    {
        WriteManifest(buildId: 1);
        SteamCmd.LatestBuild = 2;
        WriteMergedPak();
        var (vm, _, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.AutoUpdate = true;
        vm.Clock = () => DateTime.Today.AddHours(12);
        processes.Running = new RunningServer(9, DateTime.Now);
        vm.Tick();
        string? refusal = null;
        SteamCmd.DuringInstall = () =>
        {
            vm.InstallMergedPakCommand.Execute(null);
            refusal = vm.StatusMessage;
        };

        await vm.RunAutomationBeatAsync(); // the hourly check finds the newer build
        await vm.RunAutomationBeatAsync(); // nobody online: stop, update, start

        Assert.Single(SteamCmd.Installs);
        Assert.Contains("Wait until the server has finished", refusal);
        Assert.False(File.Exists(MergedPakOnServer));
    }

    /// <summary>The same gap in a manual Update: the server is stopped, SteamCMD runs, and the mod buttons stay clickable.</summary>
    [Fact]
    public async Task UpdateServer_ModChangeWhileSteamCmdRuns_IsRefused()
    {
        WriteMergedPak();
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        string? refusal = null;
        SteamCmd.DuringInstall = () =>
        {
            vm.InstallMergedPakCommand.Execute(null);
            refusal = vm.StatusMessage;
        };

        await vm.UpdateServerCommand.ExecuteAsync(null);

        Assert.Single(SteamCmd.Installs);
        Assert.Contains("Wait until the server has finished", refusal);
        Assert.False(File.Exists(MergedPakOnServer));
    }

    /// <summary>
    /// The ini was locked when the server was selected, so the form's game settings were blank;
    /// Start saves first, and that save wrote the blanks over the passwords and every setting.
    /// </summary>
    [Fact]
    public async Task Start_AfterTheIniCouldNotBeReadOnSelect_DoesNotWipeIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nJoinPassword=friends\r\nAdminPassword=adm\r\nMaxPlayers=6\r\n");
        var (vm, _, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var profile = vm.SelectedProfile!;

        using (new FileStream(IniPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            vm.SelectedProfile = null;
            vm.SelectedProfile = profile;
        }

        Assert.Equal("", vm.AdminPassword);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(processes.Started);
        var onDisk = ServerSettingsIni.Load(IniPath).Read();
        Assert.Equal("adm", onDisk.AdminPassword);
        Assert.Equal("friends", onDisk.JoinPassword);
        Assert.Equal(6, onDisk.MaxPlayers);
        Assert.Equal("adm", vm.AdminPassword); // read again into the form, for a second look

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Single(processes.Started);
        Assert.Equal("adm", ServerSettingsIni.Load(IniPath).Read().AdminPassword);
    }

    /// <summary>
    /// The ban list box is filled when the server is selected; while it ran, BanPlayer in chat added
    /// a ban. Saving an admin change afterwards put the old ban list back over it.
    /// </summary>
    [Fact]
    public void SaveAccessLists_BanAddedByTheServerMeanwhile_IsKept()
    {
        var banPath = DedicatedServerLayout.BanListPath(_install, null);
        Directory.CreateDirectory(Path.GetDirectoryName(banPath)!);
        File.WriteAllText(banPath, "76561198000000001\r\n");
        var (vm, _, _, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        File.WriteAllText(banPath, "76561198000000001\r\n76561198000000002\r\n"); // the server banned someone

        vm.AdminWhitelistText = "76561198851388131\n";
        vm.SaveAccessListsCommand.Execute(null);

        Assert.Contains("76561198000000002", File.ReadAllText(banPath));
        Assert.Contains("76561198000000002", vm.BanListText);
        Assert.Equal("76561198851388131\r\n", File.ReadAllText(DedicatedServerLayout.AdminWhitelistPath(_install, null)));
        Assert.Empty(dialogs.Messages);
    }

    /// <summary>Both changed (the box edited here, the file by the server): it asks; No saves nothing and shows the file.</summary>
    [Fact]
    public void SaveAccessLists_BanListEditedHereAndByTheServer_AsksAndNoKeepsTheFile()
    {
        var banPath = DedicatedServerLayout.BanListPath(_install, null);
        Directory.CreateDirectory(Path.GetDirectoryName(banPath)!);
        File.WriteAllText(banPath, "76561198000000001\r\n");
        var (vm, _, _, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        File.WriteAllText(banPath, "76561198000000001\r\n76561198000000002\r\n");
        vm.BanListText = "";
        dialogs.Answers.Enqueue(false);

        vm.SaveAccessListsCommand.Execute(null);

        Assert.Contains(dialogs.Messages, m => m.Contains("ban list on disk changed"));
        Assert.Contains("76561198000000002", File.ReadAllText(banPath));
        Assert.Contains("76561198000000002", vm.BanListText);
        Assert.Contains("Nothing was saved", vm.StatusMessage);
    }
}
