using System.Collections.ObjectModel;
using System.IO;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Covers RemoteModsPath/RemoteWin64Path's override-vs-default resolution — the actual behavior
/// change behind FtpSiteProfile.ModsPath/Win64Path. ServerViewModel had zero tests before this, and
/// most of its commands (Connect's own untrusted-certificate prompt, every Install/Sync command's
/// own confirmation) go through ThemedMessageBox, a real WPF window that can't run in a unit test
/// host — so these two properties are internal (see their own doc comments) and exercised here via
/// a real ConnectAsync round trip, since ConnectAsync's happy path (no untrusted certificate) never
/// touches ThemedMessageBox at all. That also makes this the regression test for "which site is the
/// live connection actually against" — SelectedSite alone doesn't answer that (see ServerViewModel's
/// own _connectedSite doc comment), so these assertions would have silently used the wrong site's
/// path if ServerViewModel still resolved off SelectedSite instead.
/// </summary>
public sealed class ServerViewModelTests
{
    private static FtpSiteProfile MakeSite(Guid id, string? modsPath = null, string? win64Path = null) => new()
    {
        Id = id, Name = "Test Site", Host = "ftp.example.com", Port = 21, Username = "icarus",
        ModsPath = modsPath, Win64Path = win64Path,
    };

    private static ServerViewModel CreateViewModel(FakeFtpSiteStore siteStore, IFtpClient ftpClient) => new(
        siteStore,
        new FakeCredentialStore(),
        () => ftpClient,
        new FakeActivityLog(),
        new FakeSettingsService(),
        new FakeUe4ssModRepository(),
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

        public string Import(string zipFilePath) => throw new NotSupportedException("Not exercised by these tests.");

        public string ImportFromFolder(string sourceFolder, string fallbackName) => throw new NotSupportedException("Not exercised by these tests.");

        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) => throw new NotSupportedException("Not exercised by these tests.");

        public string AdoptFromGame(string gameModsFolderPath, string folderName) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeActivityLog : IActivityLog
    {
        public ObservableCollection<ActivityEntry> Entries { get; } = [];

        public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info) =>
            Entries.Insert(0, new ActivityEntry(message, kind, DateTimeOffset.Now));
    }
}
