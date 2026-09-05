using System.Collections.ObjectModel;
using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Covers RemoteModsPath/RemoteWin64Path's override-vs-default resolution — the actual behavior
/// change behind FtpSiteProfile.ModsPath/Win64Path — plus the DeleteSiteAsync/SaveSite safety fixes
/// below. ServerViewModel had zero tests before this file; its six confirm dialogs originally called
/// ThemedMessageBox.Show directly (a real WPF window that can't run in a unit test host), migrated to
/// the same IDialogService seam SavesViewModel/SettingsViewModel already use specifically so
/// DeleteSiteAsync's own confirm-gated bugs below could be exercised at all. RemoteModsPath/
/// RemoteWin64Path stay internal (see their own doc comments) and are exercised via a real
/// ConnectAsync round trip — also the regression test for "which site is the live connection actually
/// against": SelectedSite alone doesn't answer that (see ServerViewModel's own _connectedSite doc
/// comment), so these assertions would have silently used the wrong site's path if ServerViewModel
/// still resolved off SelectedSite instead.
/// </summary>
public sealed class ServerViewModelTests
{
    private static FtpSiteProfile MakeSite(Guid id, string? modsPath = null, string? win64Path = null) => new()
    {
        Id = id, Name = "Test Site", Host = "ftp.example.com", Port = 21, Username = "icarus",
        ModsPath = modsPath, Win64Path = win64Path,
    };

    private static ServerViewModel CreateViewModel(FakeFtpSiteStore siteStore, IFtpClient ftpClient, FakeDialogService? dialogService = null) => new(
        siteStore,
        new FakeCredentialStore(),
        () => ftpClient,
        new FakeActivityLog(),
        new FakeSettingsService(),
        new FakeUe4ssModRepository(),
        dialogService ?? new FakeDialogService(confirmResult: true),
        Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "ISL-Merged_P.pak"));

    [Fact]
    public async Task ConnectAsync_SiteWithNoPathOverrides_ResolvesTheFixedSurvivalServersDefaults()
    {
        var site = MakeSite(Guid.NewGuid());
        var vm = CreateViewModel(new FakeFtpSiteStore([site]), new FakeFtpClient());
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.Equal("Icarus/Content/Paks/mods", vm.RemoteModsPath);
        Assert.Equal("Icarus/Binaries/Win64", vm.RemoteWin64Path);
        Assert.Equal("Icarus/Binaries/Win64/ue4ss", vm.RemoteLoaderPath);
        Assert.Equal("Icarus/Binaries/Win64/ue4ss/Mods", vm.RemoteModsRootPath);
    }

    [Fact]
    public async Task ConnectAsync_SiteWithPathOverridesSet_ResolvesTheSitesOwnPathsInstead()
    {
        var site = MakeSite(Guid.NewGuid(), modsPath: "gameserver/Content/Paks/mods", win64Path: "gameserver/Binaries/Win64");
        var vm = CreateViewModel(new FakeFtpSiteStore([site]), new FakeFtpClient());
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.Equal("gameserver/Content/Paks/mods", vm.RemoteModsPath);
        Assert.Equal("gameserver/Binaries/Win64", vm.RemoteWin64Path);
        // The loader/mods-root paths derive from Win64Path rather than being independent overrides
        // of their own, so a Win64Path override moves them along with it.
        Assert.Equal("gameserver/Binaries/Win64/ue4ss", vm.RemoteLoaderPath);
        Assert.Equal("gameserver/Binaries/Win64/ue4ss/Mods", vm.RemoteModsRootPath);
    }

    [Fact]
    public void RemoteModsPath_BeforeAnyConnection_ResolvesTheFixedDefaultsRatherThanThrowing()
    {
        // No SelectedSite, no ConnectAsync — _connectedSite is still null, same as right after
        // app startup. Guards against a null-reference regression in the override-or-default check.
        var vm = CreateViewModel(new FakeFtpSiteStore([]), new FakeFtpClient());

        Assert.Equal("Icarus/Content/Paks/mods", vm.RemoteModsPath);
        Assert.Equal("Icarus/Binaries/Win64", vm.RemoteWin64Path);
    }

    [Fact]
    public async Task DisconnectAsync_AfterConnectingToASiteWithOverrides_RevertsToTheFixedDefaults()
    {
        var site = MakeSite(Guid.NewGuid(), modsPath: "gameserver/Content/Paks/mods", win64Path: "gameserver/Binaries/Win64");
        var vm = CreateViewModel(new FakeFtpSiteStore([site]), new FakeFtpClient());
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.Equal("Icarus/Content/Paks/mods", vm.RemoteModsPath);
        Assert.Equal("Icarus/Binaries/Win64", vm.RemoteWin64Path);
    }

    /// <summary>
    /// Regression test: DisconnectAsync used to be the one FTP-touching command in this class that
    /// never set IsBusy=true around its own body, unlike every other one (see the class's own IsBusy
    /// doc comment) — so every IsEnabled="{Binding IsNotBusy}" button stayed clickable for the full
    /// duration of its two awaits, and _connectedClient stayed non-null until the very end, letting a
    /// command like Refresh genuinely race Disconnect's own DisconnectAsync/DisposeAsync calls on the
    /// same client instance. GatedFtpClient's own DisconnectAsync blocks on a TaskCompletionSource so
    /// this test can observe IsBusy mid-flight, before the fix would have already flipped it back.
    /// </summary>
    [Fact]
    public async Task DisconnectAsync_WhileInFlight_SetsIsBusySoOtherCommandsCantRaceIt()
    {
        var site = MakeSite(Guid.NewGuid());
        var gatedClient = new GatedFtpClient();
        var vm = CreateViewModel(new FakeFtpSiteStore([site]), gatedClient);
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);

        var disconnectTask = vm.DisconnectCommand.ExecuteAsync(null);
        await gatedClient.EnteredDisconnect.Task;

        Assert.True(vm.IsBusy);
        Assert.True(vm.IsConnected);

        gatedClient.ReleaseDisconnect.SetResult();
        await disconnectTask;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsConnected);
    }

    /// <summary>
    /// Regression guard: DeleteSiteAsync used to compare SelectedSite's Id to itself — the deleted
    /// site IS SelectedSite (it's literally extracted from it), so "is the connected site the one
    /// being deleted?" always read true whenever ANY site was connected. Deleting a completely
    /// unrelated, never-connected site would incorrectly disconnect from whatever site the live
    /// connection actually belonged to. Fixed by comparing against _connectedSite instead.
    /// </summary>
    [Fact]
    public async Task DeleteSiteAsync_DeletingAnUnrelatedSite_DoesNotDisconnectFromTheActuallyConnectedSite()
    {
        var connectedSite = MakeSite(Guid.NewGuid());
        var otherSite = MakeSite(Guid.NewGuid());
        var vm = CreateViewModel(new FakeFtpSiteStore([connectedSite, otherSite]), new FakeFtpClient());
        vm.SelectedSite = vm.Sites.Single(s => s.Id == connectedSite.Id);
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);

        vm.SelectedSite = vm.Sites.Single(s => s.Id == otherSite.Id);
        await vm.DeleteSiteCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.DoesNotContain(vm.Sites, s => s.Id == otherSite.Id);
        Assert.Contains(vm.Sites, s => s.Id == connectedSite.Id);
    }

    /// <summary>
    /// Regression guard: DeleteSiteAsync used to have no IsBusy guard at all, unlike every other
    /// FTP-touching command in this class — a click landing while another operation (e.g. a
    /// Disconnect) was still genuinely in flight would still delete the site's saved profile +
    /// password out from under it, even though the nested DisconnectAsync call it used to make would
    /// itself silently no-op (its own IsBusy guard refuses to run while already busy).
    /// </summary>
    [Fact]
    public async Task DeleteSiteAsync_WhileAnotherOperationIsBusy_RefusesRatherThanDeletingAnyway()
    {
        var site = MakeSite(Guid.NewGuid());
        var gatedClient = new GatedFtpClient();
        var vm = CreateViewModel(new FakeFtpSiteStore([site]), gatedClient);
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);

        var disconnectTask = vm.DisconnectCommand.ExecuteAsync(null);
        await gatedClient.EnteredDisconnect.Task;
        Assert.True(vm.IsBusy);

        await vm.DeleteSiteCommand.ExecuteAsync(null);

        Assert.Single(vm.Sites);
        Assert.Contains(vm.Sites, s => s.Id == site.Id);

        gatedClient.ReleaseDisconnect.SetResult();
        await disconnectTask;
    }

    /// <summary>
    /// Regression guard: SaveSite used to carry forward TrustedCertificateThumbprint/SupportsDelete
    /// from SelectedSite — but RememberDeleteCapability (and the certificate-trust prompt) write
    /// their freshly learned facts onto _connectedSite specifically, which can be a DIFFERENT object
    /// than SelectedSite even for "the same" site (SaveSite's own ReloadSites()+reassignment at the
    /// end of every save replaces SelectedSite with a freshly-loaded copy, while _connectedSite stays
    /// whatever object Connect originally captured). A later SaveSite reading the stale SelectedSite
    /// copy would silently revert the fact RememberDeleteCapability just learned and persisted.
    /// </summary>
    [Fact]
    public async Task SaveSite_AfterRememberDeleteCapabilityLearnedTrue_DoesNotRevertItViaAStaleSelectedSiteCopy()
    {
        var site = MakeSite(Guid.NewGuid());
        var siteStore = new FakeFtpSiteStore([site]);
        var vm = CreateViewModel(siteStore, new DeletingFtpClient());
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);

        // An unrelated save first — SaveSite's own ReloadSites()+reassignment at the end replaces
        // SelectedSite with a freshly-loaded object, distinct from _connectedSite (still the object
        // Connect originally captured), exactly the divergence RememberDeleteCapability's own doc
        // comment describes.
        vm.RemotePathInput = "/some/other/path";
        vm.SaveSiteCommand.Execute(null);

        // A real delete succeeds against the live connection, teaching RememberDeleteCapability
        // SupportsDelete=true — written onto _connectedSite, not onto the now-stale SelectedSite.
        vm.SelectedRemoteEntry = new FtpEntry("some-file.txt", false, 10, null);
        await vm.DeleteRemoteCommand.ExecuteAsync(null);

        // Saving again (e.g. a second unrelated field edit) must not silently revert the fact just
        // learned above.
        vm.RemotePathInput = "/yet/another/path";
        vm.SaveSiteCommand.Execute(null);

        Assert.True(siteStore.GetAll().Single(s => s.Id == site.Id).SupportsDelete);
    }

    private sealed class FakeFtpSiteStore(IEnumerable<FtpSiteProfile> sites) : IFtpSiteStore
    {
        private readonly List<FtpSiteProfile> _sites = sites.ToList();

        public IReadOnlyList<FtpSiteProfile> GetAll() => _sites;

        public void Save(FtpSiteProfile site)
        {
            _sites.RemoveAll(s => s.Id == site.Id);
            _sites.Add(site);
        }

        public void Delete(Guid id) => _sites.RemoveAll(s => s.Id == id);
    }

    /// <summary>A successful, instant "connection" — no untrusted certificate, no real socket — so
    /// ConnectAsync's happy path never reaches ThemedMessageBox (see this file's own class doc
    /// comment for why that matters).</summary>
    private sealed class FakeFtpClient : IFtpClient
    {
        public Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FtpEntry>>([]);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Same shape as FakeFtpClient, but DeleteFileAsync succeeds instead of throwing — lets a test exercise RememberDeleteCapability's own real success path.</summary>
    private sealed class DeletingFtpClient : IFtpClient
    {
        public Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FtpEntry>>([]);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Same instant-connect happy path as FakeFtpClient, but DisconnectAsync blocks on a
    /// TaskCompletionSource until the test releases it — lets a test observe ServerViewModel's own
    /// state (IsBusy, IsConnected) WHILE a disconnect is genuinely still in flight, not just before
    /// and after.</summary>
    private sealed class GatedFtpClient : IFtpClient
    {
        public TaskCompletionSource EnteredDisconnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisconnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FtpEntry>>([]);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public async Task DisconnectAsync()
        {
            EnteredDisconnect.SetResult();
            await ReleaseDisconnect.Task;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public void Save(string target, string secret) { }

        public string? Read(string target) => null;

        public void Delete(string target) { }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public bool Save() => true;
    }

    private sealed class FakeUe4ssModRepository : IUe4ssModRepository
    {
        public IReadOnlyList<string> GetAll() => throw new NotSupportedException("Not exercised by these tests.");

        public string Import(string zipFilePath, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");

        public string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");

        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) => throw new NotSupportedException("Not exercised by these tests.");

        public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeActivityLog : IActivityLog
    {
        public ObservableCollection<ActivityEntry> Entries { get; } = [];

        public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info) =>
            Entries.Insert(0, new ActivityEntry(message, kind, DateTimeOffset.Now));
    }

    /// <summary>Same shape as SavesViewModelTests/SettingsViewModelTests' own FakeDialogService — this is what makes DeleteSiteAsync's confirm dialog (previously a real ThemedMessageBox.Show, unrunnable in a test host) testable at all.</summary>
    private sealed class FakeDialogService(bool confirmResult) : IDialogService
    {
        public int ConfirmCallCount { get; private set; }

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            ConfirmCallCount++;
            return confirmResult;
        }

        public RenamePromptResult PromptRename(
            string currentName,
            string description = "",
            string? resetValue = null,
            string resetLabel = "",
            string resetTooltip = "",
            string title = "",
            string fieldLabel = "") => new(true, null);
    }
}
