using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Sending the player to a Nexus page when the browser doesn't open, and one file list at a time.</summary>
public sealed partial class DownloadsViewModelTests
{
    /// <summary>Regression: a free account's Update said "on the page that opened" when no browser opened, and never gave the address.</summary>
    [Fact]
    public async Task Update_FreeAccount_BrowserDoesntOpen_GivesTheAddressInstead()
    {
        var harness = ModWithAnUpdate(premium: false);
        var vm = harness.Build();
        vm.OpenUrl = _ => new InvalidOperationException("No browser is set up.");

        await vm.ResolvePrimaryFileAndFetchAsync(400, "Main");

        Assert.Contains("https://www.nexusmods.com/icarus/mods/400?tab=files&file_id=30&nmm=1", vm.PendingDownloadStatusMessage);
        Assert.DoesNotContain("page that opened", vm.PendingDownloadStatusMessage);
    }

    /// <summary>Regression: a download Nexus refused said "Opened the file's Nexus page" even when the browser didn't open.</summary>
    [Fact]
    public async Task Download_RefusedByNexus_BrowserDoesntOpen_GivesTheAddressInstead()
    {
        var harness = new Harness(_root);
        harness.NexusApi.DownloadLinksError = new NexusDownloadRejectedException("Nexus only gives download links to Premium accounts.");
        var vm = harness.Build();
        vm.OpenUrl = _ => new InvalidOperationException("No browser is set up.");

        await vm.FetchAndDownloadAsync("nxm://icarus/mods/12/files/34");

        Assert.Contains("https://www.nexusmods.com/icarus/mods/12?tab=files&file_id=34&nmm=1", vm.PendingDownloadStatusMessage);
        Assert.DoesNotContain("Opened", vm.PendingDownloadStatusMessage);
    }

    /// <summary>
    /// Regression: a second Update that got its file list while the first one's list was still
    /// open opened another list on top of it.
    /// </summary>
    [Fact]
    public async Task Update_WhileAFileListIsOpen_DoesntOpenASecondOne()
    {
        var harness = ModWithAnUpdate(premium: true);
        var vm = harness.Build();
        string? secondStatus = null;
        var dialogs = harness.Dialogs!;
        dialogs.WhileFileListIsOpen = () =>
        {
            dialogs.WhileFileListIsOpen = null;
            vm.ResolvePrimaryFileAndFetchAsync(400, "Main").GetAwaiter().GetResult();
            secondStatus = vm.PendingDownloadStatusMessage;
        };

        await vm.ResolvePrimaryFileAndFetchAsync(400, "Main");

        Assert.Equal(1, dialogs.PickNexusFileCalls);
        Assert.Contains("Already choosing a file for 'Main'", secondStatus);
    }
}
