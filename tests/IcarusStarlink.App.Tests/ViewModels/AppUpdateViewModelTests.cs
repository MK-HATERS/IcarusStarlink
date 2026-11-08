using IcarusStarlink.App.Tests.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Core.Nexus;

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
    /// same version. With none to be had, nothing is opened or downloaded.
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

    private const string GitHubZip = "https://github.example/x.zip";

    private const string AGivenLink = "nxm://icarus/mods/330/files/500?key=abc&expires=1700000000&user_id=1";

    /// <summary>HttpClient's own timeout: a TaskCanceledException nobody asked for.</summary>
    private static TaskCanceledException HttpTimeout() =>
        new("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());

    private static FakeGitHubUpdates GitHubWithChecksum() => new() { ReleaseDigest = "sha256:" + new string('a', 64) };

    /// <summary>
    /// Nexus not answering in time (HttpClient's timeout) ended the update with "Cancelled — nothing
    /// was changed.", as if the user had pressed Cancel.
    /// </summary>
    [Fact]
    public async Task GivenNexusLink_NexusTimesOut_SaysSoNotCancelled()
    {
        var nexus = new FakeStarlinkNexus { DownloadLinksError = HttpTimeout() };
        var release = new AppUpdateRelease("3.3.0", "", "", AssetDigest: null, NexusFileId: 500);
        var viewModel = new AppUpdateViewModel(release, GitHubWithChecksum(), nexus, "key", () => null, skipOldExe: false, _ => { },
            IcarusStarlink.Core.Nexus.NxmUrl.Parse(AGivenLink));

        Assert.False(await viewModel.RunAsync());

        Assert.DoesNotContain("Cancelled", viewModel.ErrorText);
        Assert.Contains("in time", viewModel.ErrorText);
    }

    /// <summary>
    /// The account check timing out escaped as a cancel and ended the update, instead of falling
    /// back to the Nexus page's own download button, which works for every account.
    /// </summary>
    [Fact]
    public async Task ValidateKeyTimesOut_FallsBackToTheDownloadButton()
    {
        var nexus = new FakeStarlinkNexus { ValidateKeyError = HttpTimeout() };
        var opened = new List<string>();
        var release = new AppUpdateRelease("3.3.0", "", GitHubZip, AssetDigest: null, NexusFileId: 500);
        var viewModel = new AppUpdateViewModel(release, GitHubWithChecksum(), nexus, "key", () => null, skipOldExe: false, opened.Add);

        var run = viewModel.RunAsync();
        Assert.True(viewModel.IsWaitingForNexus);
        Assert.Single(opened);

        viewModel.UseGitHubInsteadCommand.Execute(null);
        Assert.False(await run);
    }

    [Fact]
    public async Task UserCancel_StillSaysCancelled()
    {
        var release = new AppUpdateRelease("3.3.0", "", GitHubZip, AssetDigest: null, NexusFileId: 500);
        var viewModel = new AppUpdateViewModel(release, GitHubWithChecksum(), new FakeStarlinkNexus(), "key", () => null, skipOldExe: false, _ => { });

        var run = viewModel.RunAsync();
        Assert.True(viewModel.IsWaitingForNexus);
        viewModel.CancelOrCloseCommand.Execute(null);

        Assert.False(await run);
        Assert.StartsWith("Cancelled", viewModel.ErrorText);
    }

    /// <summary>Cancel gave no sign it was heard while the update unpacked (that can take seconds).</summary>
    [Fact]
    public async Task Cancel_SaysCancellingAtOnce()
    {
        var release = new AppUpdateRelease("3.3.0", "", GitHubZip, AssetDigest: null, NexusFileId: 500);
        var viewModel = new AppUpdateViewModel(release, GitHubWithChecksum(), new FakeStarlinkNexus(), "key", () => null, skipOldExe: false, _ => { });

        var run = viewModel.RunAsync();
        viewModel.CancelOrCloseCommand.Execute(null);

        Assert.Equal("Cancelling…", viewModel.ProgressText);
        Assert.False(await run);
    }

    /// <summary>A Nexus Premium update whose download link Nexus refuses (a quarantined file, say).</summary>
    private static AppUpdateViewModel PremiumWhoseNexusLinkFails(FakeStarlinkNexus nexus, FakeGitHubUpdates gitHub, string gitHubUrl) =>
        new(new AppUpdateRelease("3.3.0", "", gitHubUrl, AssetDigest: null, NexusFileId: 500), gitHub, nexus, "key",
            () => "test: stop before the updater starts", skipOldExe: false, _ => { });

    private static FakeStarlinkNexus PremiumNexusThatRefuses() =>
        new() { IsPremium = true, DownloadLinksError = new System.Net.Http.HttpRequestException("This file is quarantined.") };

    /// <summary>
    /// A Nexus download that failed twice in the update window left only Cancel: the same release
    /// on GitHub is offered once the user's own Try again has failed too, and only then.
    /// </summary>
    [Fact]
    public async Task NexusFailsAgainAfterTryAgain_OffersGitHub_WhichDownloadsFromGitHub()
    {
        var nexus = PremiumNexusThatRefuses();
        var gitHub = GitHubWithChecksum();
        var viewModel = PremiumWhoseNexusLinkFails(nexus, gitHub, GitHubZip);

        Assert.False(await viewModel.RunAsync());
        Assert.Contains("quarantined", viewModel.ErrorText);
        Assert.True(viewModel.CanRetry);
        Assert.False(viewModel.CanUseGitHubInstead);

        await viewModel.TryAgainCommand.ExecuteAsync(null);
        Assert.Equal([500, 500], nexus.DownloadLinkFileIds);
        Assert.True(viewModel.CanUseGitHubInstead);

        // Never by itself: nothing came from GitHub until the user chose it.
        Assert.Empty(gitHub.Downloads);

        await viewModel.UseGitHubInsteadCommand.ExecuteAsync(null);
        Assert.Equal([GitHubZip], Assert.Single(gitHub.Downloads));
        Assert.Equal("From GitHub instead", viewModel.Steps[0].Detail);
        Assert.Equal(2, nexus.DownloadLinkFileIds.Count);
    }

    [Fact]
    public async Task NexusFailsTwice_WithNoGitHubRelease_OffersOnlyTryAgain()
    {
        var viewModel = PremiumWhoseNexusLinkFails(PremiumNexusThatRefuses(), GitHubWithChecksum(), gitHubUrl: "");

        Assert.False(await viewModel.RunAsync());
        await viewModel.TryAgainCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanRetry);
        Assert.False(viewModel.CanUseGitHubInstead);
    }

    /// <summary>nxm:// links as Windows has them: whether this Starlink handles them, and who does.</summary>
    private sealed class FakeNxmRegistrar(string? currentHandler) : INxmProtocolRegistrar
    {
        public string? Current { get; private set; } = currentHandler;

        public bool Registered { get; private set; }

        public bool Throws { get; init; }

        public bool IsRegisteredToThisApp() => Throws ? throw new UnauthorizedAccessException("registry refused") : Registered;

        public string? CurrentHandlerCommand() => Current;

        public string? PreviousHandlerCommand() => null;

        public void Register()
        {
            Registered = true;
            Current = "\"C:\\Games\\IcarusStarlink\\IcarusStarlink.exe\" \"%1\"";
        }

        public void Unregister() => Registered = false;
    }

    private const string Vortex = "\"C:\\Program Files\\Black Tree Gaming Ltd\\Vortex\\Vortex.exe\" -d \"%1\"";

    /// <summary>
    /// With nxm:// links going to Vortex (or nothing), the window opened Nexus and waited forever,
    /// promising "Starlink takes it from there": the download went to the other app. Now it says so
    /// and opens Nexus only once Starlink is the handler.
    /// </summary>
    [Fact]
    public async Task NotTheDownloadHandler_SaysSo_AndOpensNexusOnlyOnceStarlinkIsTheHandler()
    {
        var registrar = new FakeNxmRegistrar(Vortex);
        var opened = new List<string>();
        var release = new AppUpdateRelease("3.3.0", "", "", AssetDigest: null, NexusFileId: 500);
        var viewModel = new AppUpdateViewModel(release, GitHubWithChecksum(), new FakeStarlinkNexus(), "key", () => null, skipOldExe: false, opened.Add,
            nxmRegistrar: registrar);

        var run = viewModel.RunAsync();

        Assert.True(viewModel.IsWaitingForNexus);
        Assert.True(viewModel.CanMakeStarlinkTheDownloadHandler);
        Assert.False(viewModel.CanOpenNexusPageAgain);
        Assert.Contains("Make Starlink the download handler", viewModel.Steps[0].Detail);
        Assert.DoesNotContain("takes it from there", viewModel.Steps[0].Detail);
        Assert.Empty(opened);

        viewModel.MakeStarlinkTheDownloadHandlerCommand.Execute(null);

        Assert.True(registrar.Registered);
        Assert.False(viewModel.CanMakeStarlinkTheDownloadHandler);
        Assert.Single(opened);
        Assert.Contains("takes it from there", viewModel.Steps[0].Detail);

        viewModel.CancelOrCloseCommand.Execute(null);
        Assert.False(await run);
    }

    /// <summary>Another Starlink install holding the links passes Starlink's own links on to this one.</summary>
    [Theory]
    [InlineData("\"D:\\Other\\IcarusStarlink.exe\" \"%1\"", false)]
    [InlineData(Vortex, true)]
    public async Task AnotherStarlinkInstallHasTheLinks_CountsAsHandled_AndAFailedCheckAssumesSo(string handler, bool checkThrows)
    {
        var opened = new List<string>();
        var release = new AppUpdateRelease("3.3.0", "", "", AssetDigest: null, NexusFileId: 500);
        var viewModel = new AppUpdateViewModel(release, GitHubWithChecksum(), new FakeStarlinkNexus(), "key", () => null, skipOldExe: false, opened.Add,
            nxmRegistrar: new FakeNxmRegistrar(handler) { Throws = checkThrows });

        var run = viewModel.RunAsync();

        Assert.False(viewModel.CanMakeStarlinkTheDownloadHandler);
        Assert.Single(opened);

        viewModel.CancelOrCloseCommand.Execute(null);
        Assert.False(await run);
    }
}
