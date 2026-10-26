using IcarusStarlink.App.Tests.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed class AppUpdateViewModelTests
{
    /// <summary>
    /// The update window opens Starlink's Nexus page on the new file and waits for the user's
    /// download. Pressing the download on another file there (an older version further down the
    /// Files tab) was taken as the update: with no GitHub checksum for the version, that file was
    /// installed under the "Updating to vX" heading, a downgrade.
    /// </summary>
    [Fact]
    public async Task WaitingForNexus_ADownloadOfAnotherFile_IsNotTakenAsTheUpdate()
    {
        var nexus = new FakeStarlinkNexus();
        var opened = new List<string>();
        var release = new AppUpdateRelease("3.3.0", "", "", AssetDigest: null, NexusFileId: 500);
        var viewModel = new AppUpdateViewModel(release, new FakeGitHubUpdates { ReleaseDigest = "sha256:" + new string('a', 64) }, nexus, "key", () => null, skipOldExe: false, opened.Add);

        var run = viewModel.RunAsync();
        Assert.True(viewModel.IsWaitingForNexus);
        Assert.Single(opened);

        Assert.True(viewModel.TryDeliverNexusLink("nxm://icarus/mods/330/files/400?key=abc&expires=1700000000&user_id=1"));
        await Task.Delay(100);

        Assert.Empty(nexus.DownloadLinkFileIds);
        Assert.True(viewModel.IsWaitingForNexus);
        Assert.Contains("different file", viewModel.Steps[0].Detail);

        // The right file still goes through.
        Assert.True(viewModel.TryDeliverNexusLink("nxm://icarus/mods/330/files/500?key=abc&expires=1700000000&user_id=1"));
        Assert.False(await run);
        Assert.Equal([500], nexus.DownloadLinkFileIds);
    }

    /// <summary>
    /// A Nexus download is only installed once it can be checked against GitHub's checksum for the
    /// same version (user's call 2026-10-26). With none to be had, nothing is opened or downloaded.
    /// </summary>
    [Fact]
    public async Task FromNexus_WithNoGitHubChecksum_StopsBeforeDownloading()
    {
        var nexus = new FakeStarlinkNexus();
        var gitHub = new FakeGitHubUpdates();
        var opened = new List<string>();
        var release = new AppUpdateRelease("3.3.0", "", "", AssetDigest: null, NexusFileId: 500);
        var viewModel = new AppUpdateViewModel(release, gitHub, nexus, "key", () => null, skipOldExe: false, opened.Add);

        Assert.False(await viewModel.RunAsync());

        Assert.Empty(opened);
        Assert.Empty(gitHub.Downloads);
        Assert.Empty(nexus.DownloadLinkFileIds);
        Assert.Contains(viewModel.Steps, s => s.State == "failed");
    }
}
