using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Update all fetches the newer file each check found, and a free account gets one Nexus page at a time.</summary>
public sealed partial class LibraryViewModelTests
{
    /// <summary>Mods 1 and 2, each with two variant files: file 10 → 11 and 20 → 21 are the updates of the installed ones.</summary>
    private static (LibraryViewModel Vm, TestHarness Harness) BuildTwoOutdatedNexusFiles(bool premium)
    {
        var harness = new TestHarness();
        harness.CredentialStore.ApiKey = "key";
        harness.NexusApiClient.User = new NexusUserInfo(5, "me", IsPremium: premium, IsSupporter: false, "", "");
        harness.NexusApiClient.FileSets[1] = new([NexusFile(11, "A", "2.0"), NexusFile(12, "A other", "2.0")], [new NexusFileUpdate(10, 11)]);
        harness.NexusApiClient.FileSets[2] = new([NexusFile(21, "B", "2.0"), NexusFile(22, "B other", "2.0")], [new NexusFileUpdate(20, 21)]);
        harness.AddMod("ModA", source: "Nexus", nexusModId: 1).NexusFileId = 10;
        harness.AddMod("ModB", source: "Nexus", nexusModId: 2).NexusFileId = 20;
        var vm = harness.Build();
        vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA").LatestNexusFileId = 11;
        vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModB").LatestNexusFileId = 21;
        Assert.All(vm.RootItems.OfType<LibraryItemViewModel>(), i => Assert.True(i.HasUpdateAvailable));
        return (vm, harness);
    }

    /// <summary>Regression: Update all asked only for the mod, so a mod with several files opened the file list (or took another file) instead of the newer one the check found.</summary>
    [Fact]
    public async Task UpdateAll_Premium_FetchesTheNewerFileIdsWithoutTheFileList()
    {
        var (vm, harness) = BuildTwoOutdatedNexusFiles(premium: true);

        await vm.UpdateAllCommand.ExecuteAsync(null);

        Assert.Empty(harness.DialogService.NexusFilePicks);
        Assert.Equal([(1, 11), (2, 21)], harness.NexusApiClient.DownloadLinkRequests);
    }

    /// <summary>
    /// A free account: each newer file's Nexus page opens, one at a time; the next opens only
    /// after the last one's download has come back from Nexus (its nxm:// link) and installed.
    /// </summary>
    [Fact]
    public async Task UpdateAll_FreeAccount_OpensOnePageAtATime_AndMovesOnOnlyAfterItsDownloadArrives()
    {
        var (vm, harness) = BuildTwoOutdatedNexusFiles(premium: false);
        var opened = new List<string>();
        vm.Downloads.OpenUrl = url => { lock (opened) { opened.Add(url); } return null; };
        int OpenedCount() { lock (opened) { return opened.Count; } }

        var updateAll = vm.UpdateAllCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => OpenedCount() == 1);
        await Task.Delay(100);
        Assert.Equal(1, OpenedCount());
        Assert.False(updateAll.IsCompleted);

        await vm.Downloads.FetchAndDownloadAsync("nxm://icarus/mods/1/files/11?key=k&expires=1");
        await WaitUntilAsync(() => OpenedCount() == 2);
        Assert.False(updateAll.IsCompleted);

        await vm.Downloads.FetchAndDownloadAsync("nxm://icarus/mods/2/files/21?key=k&expires=1");
        await updateAll.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            ["https://www.nexusmods.com/icarus/mods/1?tab=files&file_id=11&nmm=1", "https://www.nexusmods.com/icarus/mods/2?tab=files&file_id=21&nmm=1"],
            opened);
        Assert.Empty(harness.DialogService.NexusFilePicks);
        Assert.Contains("ModA", vm.StatusMessage);
        Assert.Contains("ModB", vm.StatusMessage);
    }

    /// <summary>Stop waiting on the Downloads page ends Update all there: the next page doesn't open.</summary>
    [Fact]
    public async Task UpdateAll_FreeAccount_StopWaiting_DoesntOpenTheNextPage()
    {
        var (vm, _) = BuildTwoOutdatedNexusFiles(premium: false);
        var opened = new List<string>();
        vm.Downloads.OpenUrl = url => { lock (opened) { opened.Add(url); } return null; };

        var updateAll = vm.UpdateAllCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.Downloads.IsWaitingForBrowserDownload);
        vm.Downloads.StopWaitingForBrowserDownloadCommand.Execute(null);
        await updateAll.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(opened);
        Assert.Contains("Stopped at 'ModA'", vm.StatusMessage);
        Assert.Contains("ModB", vm.StatusMessage);
    }
}
