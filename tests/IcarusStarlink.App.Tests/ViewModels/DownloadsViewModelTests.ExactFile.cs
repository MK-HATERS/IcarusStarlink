using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Fetching an exact Nexus file (Update all, Import patch), and a free account's wait for the download from the Nexus page.</summary>
public sealed partial class DownloadsViewModelTests
{
    [Fact]
    public async Task FetchExactFile_Premium_DownloadsThatFileWithoutTheFileList()
    {
        var harness = ModWithAnUpdate(premium: true);
        var vm = harness.Build();
        vm.OpenUrl = _ => null;

        var outcome = await vm.FetchNexusModAsync(400, "Main", fileId: 20, waitForBrowserDownload: false);

        Assert.Equal(NexusFetchOutcome.Fetched, outcome);
        Assert.Equal(0, harness.Dialogs!.PickNexusFileCalls);
        Assert.Equal([20], harness.NexusApi.DownloadLinkFileIds);
    }

    /// <summary>A file the author removed from Nexus: the file list opens with its newer version picked.</summary>
    [Fact]
    public async Task FetchExactFile_GoneFromNexus_OpensTheFileListWithItsNewerVersionPicked()
    {
        var harness = ModWithAnUpdate(premium: true);
        var vm = harness.Build();
        vm.OpenUrl = _ => null;

        var outcome = await vm.FetchNexusModAsync(400, "Main", fileId: 10, waitForBrowserDownload: false);

        Assert.Equal(NexusFetchOutcome.Cancelled, outcome); // the fake list answers Cancel
        Assert.Equal(1, harness.Dialogs!.PickNexusFileCalls);
        Assert.Equal(30, harness.Dialogs.LastPreselect?.FileId);
    }

    /// <summary>A free account: that file's page opens, and the wait ends only when that mod's download has come back from it.</summary>
    [Fact]
    public async Task FetchExactFile_FreeAccount_OpensItsPageAndWaitsForItsDownload()
    {
        var harness = ModWithAnUpdate(premium: false);
        var vm = harness.Build();
        var opened = new List<string>();
        vm.OpenUrl = url => { opened.Add(url); return null; };

        var fetch = vm.FetchNexusModAsync(400, "Main", fileId: 20, waitForBrowserDownload: true);
        Assert.Equal(["https://www.nexusmods.com/icarus/mods/400?tab=files&file_id=20&nmm=1"], opened);
        Assert.True(vm.IsWaitingForBrowserDownload);

        // Another mod's download doesn't end the wait.
        await vm.FetchAndDownloadAsync("nxm://icarus/mods/12/files/34?key=k&expires=1");
        await Task.Delay(50);
        Assert.False(fetch.IsCompleted);

        await vm.FetchAndDownloadAsync("nxm://icarus/mods/400/files/20?key=k&expires=1");

        Assert.Equal(NexusFetchOutcome.Arrived, await fetch.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(vm.IsWaitingForBrowserDownload);
        Assert.Equal(0, harness.Dialogs!.PickNexusFileCalls);
    }

    [Fact]
    public async Task WaitForBrowserDownload_StopWaiting_EndsTheWait()
    {
        var harness = ModWithAnUpdate(premium: false);
        var vm = harness.Build();
        vm.OpenUrl = _ => null;

        var fetch = vm.FetchNexusModAsync(400, "Main", fileId: 20, waitForBrowserDownload: true);
        Assert.True(vm.StopWaitingForBrowserDownloadCommand.CanExecute(null));
        vm.StopWaitingForBrowserDownloadCommand.Execute(null);

        Assert.Equal(NexusFetchOutcome.Stopped, await fetch.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(vm.IsWaitingForBrowserDownload);
        Assert.Contains("Stopped waiting for 'Main'", vm.PendingDownloadStatusMessage);
    }

    [Fact]
    public async Task WaitForBrowserDownload_NoDownloadComes_TimesOutAndSaysWhatToCheck()
    {
        var harness = ModWithAnUpdate(premium: false);
        var vm = harness.Build();
        vm.OpenUrl = _ => null;
        vm.BrowserDownloadTimeout = TimeSpan.FromMilliseconds(50);

        var outcome = await vm.FetchNexusModAsync(400, "Main", fileId: 20, waitForBrowserDownload: true);

        Assert.Equal(NexusFetchOutcome.TimedOut, outcome);
        Assert.Contains("Register as Nexus download handler", vm.PendingDownloadStatusMessage);
    }

    /// <summary>One wait at a time: a second one is refused, and no second page opens.</summary>
    [Fact]
    public async Task WaitForBrowserDownload_WhileWaiting_ASecondWaitIsRefused()
    {
        var harness = ModWithAnUpdate(premium: false);
        var vm = harness.Build();
        var opened = new List<string>();
        vm.OpenUrl = url => { opened.Add(url); return null; };

        var first = vm.FetchNexusModAsync(400, "Main", fileId: 20, waitForBrowserDownload: true);
        var second = await vm.FetchNexusModAsync(400, "Main", fileId: 30, waitForBrowserDownload: true);

        Assert.Equal(NexusFetchOutcome.Busy, second);
        Assert.Single(opened);
        vm.StopWaitingForBrowserDownloadCommand.Execute(null);
        Assert.Equal(NexusFetchOutcome.Stopped, await first.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
