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

    private static ServerViewModel CreateViewModel(FakeFtpSiteStore siteStore, IFtpClient ftpClient, FakeDialogService? dialogService = null, string? outputPakPath = null, ICredentialStore? credentialStore = null, ISettingsService? settings = null) => new(
        siteStore,
        credentialStore ?? new FakeCredentialStore(),
        () => ftpClient,
        new FakeActivityLog(),
        settings ?? new FakeSettingsService(),
        dialogService ?? new FakeDialogService(confirmResult: true),
        outputPakPath ?? Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "ISL-Merged_P.pak"));

    private static async Task<(ServerViewModel Vm, string Folder)> ConnectedWithBuiltPakAsync(
        RecordingFtpClient ftp, FtpSiteProfile? site = null, FakeDialogService? dialogs = null, FakeFtpSiteStore? siteStore = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ISL-Merged_9999_P.pak"), "pak");
        File.WriteAllText(Path.Combine(folder, "ISL-Merged.txt"), "manifest");
        var vm = CreateViewModel(siteStore ?? new FakeFtpSiteStore([site ?? MakeSite(Guid.NewGuid())]), ftp, dialogs, outputPakPath: Path.Combine(folder, "ISL-Merged_9999_P.pak"));
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);
        return (vm, folder);
    }

    private const string Mods = "Icarus/Content/Paks/mods/";

    /// <summary>The new pak goes up under a temporary name first; only then are the old paks deleted and the new one renamed into place.</summary>
    [Fact]
    public async Task InstallPakToServer_UploadsUnderATempNameBeforeDeletingTheOldPak()
    {
        var ftp = new RecordingFtpClient("IMM_Merged_Mod_P.pak", "ISL-Merged_9999_P.pak");
        var (vm, folder) = await ConnectedWithBuiltPakAsync(ftp);
        try
        {
            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            var firstDelete = ftp.Operations.FindIndex(o => o.StartsWith("delete ", StringComparison.Ordinal));
            var lastUpload = ftp.Operations.FindLastIndex(o => o.StartsWith("upload ", StringComparison.Ordinal));
            Assert.True(lastUpload < firstDelete, string.Join("\n", ftp.Operations));
            Assert.All(ftp.Operations.Where(o => o.StartsWith("upload ", StringComparison.Ordinal)), o => Assert.EndsWith(".uploading", o));
            // No ArcFission data pak on the server: the plain name, and the old name's copy is gone.
            Assert.Equal(
                [Mods + "ISL-Merged.txt", Mods + "ISL-Merged_P.pak"],
                ftp.Files.OrderBy(f => f, StringComparer.Ordinal));
            Assert.Contains("Installed the merged pak", vm.ConnectionStatusMessage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A connection that drops mid-upload leaves the server's old pak in place (it used to be deleted first).</summary>
    [Fact]
    public async Task InstallPakToServer_UploadFails_LeavesTheOldPakAlone()
    {
        var ftp = new RecordingFtpClient("IMM_Merged_Mod_P.pak") { FailUploadOf = "ISL-Merged_P.pak" };
        var (vm, folder) = await ConnectedWithBuiltPakAsync(ftp);
        try
        {
            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            Assert.Contains(Mods + "IMM_Merged_Mod_P.pak", ftp.Files);
            Assert.DoesNotContain(ftp.Operations, o => o.StartsWith("delete ", StringComparison.Ordinal));
            Assert.DoesNotContain(Mods + "ISL-Merged_P.pak", ftp.Files);
            Assert.Contains("failed", vm.ConnectionStatusMessage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A server that refuses renames still ends up with the new pak under its real name and no temporary copy.</summary>
    [Fact]
    public async Task InstallPakToServer_RenameRefused_UploadsUnderTheRealNameAndTidiesUp()
    {
        var ftp = new RecordingFtpClient("IMM_Merged_Mod_P.pak") { RefuseRename = true };
        var (vm, folder) = await ConnectedWithBuiltPakAsync(ftp);
        try
        {
            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            Assert.Equal(
                [Mods + "ISL-Merged.txt", Mods + "ISL-Merged_P.pak"],
                ftp.Files.OrderBy(f => f, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>ArcFission Library's data pak on the server means the library runs there: the merge goes up as zz_zz_…_9999 to outrank it, the data pak stays, and the plain-named copy goes.</summary>
    [Fact]
    public async Task InstallPakToServer_ArcFissionDataPakOnTheServer_UploadsUnderTheNameThatOutranksIt()
    {
        var ftp = new RecordingFtpClient("ZZ_ArcFission_Data_4_P.pak", "ISL-Merged_P.pak");
        var (vm, folder) = await ConnectedWithBuiltPakAsync(ftp);
        try
        {
            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            Assert.Equal(
                [Mods + "ISL-Merged.txt", Mods + "ZZ_ArcFission_Data_4_P.pak", Mods + "zz_zz_ISL-Merged_9999_P.pak"],
                ftp.Files.OrderBy(f => f, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

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

    /// <summary>Regression: correcting the connected site's Mods path and saving kept the old path for the live connection until a reconnect.</summary>
    [Fact]
    public async Task SaveSite_EditingTheConnectedSitesModsPath_AppliesToTheLiveConnection()
    {
        var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), new FakeFtpClient());
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.ModsPathInput = "gameserver/Content/Paks/mods";
        vm.SaveSiteCommand.Execute(null);

        Assert.True(vm.IsConnected);
        Assert.Equal("gameserver/Content/Paks/mods", vm.RemoteModsPath);
    }

    /// <summary>Regression: a Credential Manager failure escaped Connect (it flows exceptions to the task scheduler), so the button silently did nothing.</summary>
    [Fact]
    public async Task ConnectAsync_CredentialManagerFails_SaysSo()
    {
        var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), new FakeFtpClient(), credentialStore: new ThrowingCredentialStore());
        vm.SelectedSite = vm.Sites.Single();

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.Contains("Credential Manager", vm.ConnectionStatusMessage);
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

    /// <summary>
    /// Regression: RememberDeleteCapability wrote _connectedSite (the object captured at
    /// Connect) back to the store wholesale, so a field edited and saved AFTER connecting was
    /// silently reverted to its pre-connect value the moment a delete taught SupportsDelete.
    /// </summary>
    [Fact]
    public async Task RememberDeleteCapability_AfterAnEditSavedSinceConnect_KeepsThatEdit()
    {
        var site = MakeSite(Guid.NewGuid());
        var siteStore = new FakeFtpSiteStore([site]);
        var vm = CreateViewModel(siteStore, new DeletingFtpClient());
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.RemotePathInput = "/edited/after/connect";
        vm.SaveSiteCommand.Execute(null);

        vm.SelectedRemoteEntry = new FtpEntry("some-file.txt", false, 10, null);
        await vm.DeleteRemoteCommand.ExecuteAsync(null);

        var stored = siteStore.GetAll().Single(s => s.Id == site.Id);
        Assert.True(stored.SupportsDelete);
        Assert.Equal("/edited/after/connect", stored.RemotePath);
    }

    private sealed class FakeFtpSiteStore(IEnumerable<FtpSiteProfile> sites) : IFtpSiteStore
    {
        private readonly List<FtpSiteProfile> _sites = sites.ToList();

        public IReadOnlyList<FtpSiteProfile> GetAll() => _sites;

        /// <summary>Save throws, as when ftp_sites.json is held by a scanner or a sync client.</summary>
        public bool ThrowOnSave { get; set; }

        public void Save(FtpSiteProfile site)
        {
            if (ThrowOnSave)
            {
                throw new IOException("The process cannot access the file 'ftp_sites.json' because it is being used by another process.");
            }

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
        /// <summary>Thrown by ConnectAsync (a refused login, say). Null: the connect works.</summary>
        public Exception? ConnectFailure { get; init; }

        public bool IsEncrypted { get; init; }

        public Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default) =>
            ConnectFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;

        public Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FtpEntry>>([]);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task RenameFileAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Same shape as FakeFtpClient, but DeleteFileAsync succeeds instead of throwing — lets a test exercise RememberDeleteCapability's own real success path.</summary>
    private sealed class DeletingFtpClient : IFtpClient
    {
        public Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool IsEncrypted => false;

        public Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FtpEntry>>([]);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RenameFileAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken = default) =>
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

        public bool IsEncrypted => false;

        public Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FtpEntry>>([]);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task RenameFileAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public async Task DisconnectAsync()
        {
            EnteredDisconnect.SetResult();
            await ReleaseDisconnect.Task;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An FTP server in memory: records every operation in order, and can fail an upload part-way or refuse renames.</summary>
    private sealed class RecordingFtpClient(params string[] filesInMods) : IFtpClient
    {
        public HashSet<string> Files { get; } = new(filesInMods.Select(f => "Icarus/Content/Paks/mods/" + f), StringComparer.OrdinalIgnoreCase);
        public List<string> Operations { get; } = [];
        public string? FailUploadOf { get; set; }
        public bool RefuseRename { get; set; }

        /// <summary>Every delete is refused, as on a host that blocks delete (SurvivalServers); uploads over an existing name still work.</summary>
        public bool RefuseDelete { get; set; }

        public Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool IsEncrypted => false;

        public Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FtpEntry>>([.. Files.Where(f => f.StartsWith(remotePath + "/", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FtpEntry(f[(remotePath.Length + 1)..], false, 1, null))]);

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
        {
            Operations.Add("upload " + remotePath);
            if (FailUploadOf is { } failing && remotePath.Contains(failing, StringComparison.OrdinalIgnoreCase))
            {
                Files.Add(remotePath); // a partial file is left behind
                throw new IOException("Connection reset mid-upload.");
            }

            Files.Add(remotePath);
            return Task.CompletedTask;
        }

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            Operations.Add("delete " + remotePath);
            if (RefuseDelete)
            {
                throw new FtpOperationRejectedException("550 Permission denied");
            }

            Files.Remove(remotePath);
            return Task.CompletedTask;
        }

        public Task RenameFileAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken = default)
        {
            Operations.Add($"rename {fromRemotePath} -> {toRemotePath}");
            if (RefuseRename)
            {
                throw new FtpOperationRejectedException("550 Rename not allowed");
            }

            Files.Remove(fromRemotePath);
            Files.Add(toRemotePath);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string RemoteUe4ss = "Icarus/Binaries/Win64/ue4ss";

    /// <summary>A fake game install: Content\, and Binaries\Win64\ue4ss beside it.</summary>
    private static (string Folder, FakeSettingsService Settings) GameWithUe4ss()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        var content = Path.Combine(folder, "Icarus", "Content");
        Directory.CreateDirectory(content);
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = content;
        return (folder, settings);
    }

    private static void WriteFile(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static async Task<ServerViewModel> ConnectedAsync(IFtpClient ftp, ISettingsService settings)
    {
        var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), ftp, settings: settings);
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>Only mods that are on in the game go up (not switched-off ones, not the framework's), and they are switched on in the server's mods.txt, whose other lines stay.</summary>
    [Fact]
    public async Task SyncUe4ssMods_UploadsOnlyModsOnInTheGame_AndTurnsThemOnInTheServersModsTxt()
    {
        var (folder, settings) = GameWithUe4ss();
        try
        {
            var mods = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveModsFolder(settings.Current.IcarusContentPath!);
            WriteFile(Path.Combine(mods, "mods.txt"), "OnMod : 1\r\nOffMod : 0\r\nBPModLoaderMod : 1\r\n");
            WriteFile(Path.Combine(mods, "OnMod", "Scripts", "main.lua"));
            WriteFile(Path.Combine(mods, "OnMod", "OnMod.log"));
            WriteFile(Path.Combine(mods, "OffMod", "Scripts", "main.lua"));
            WriteFile(Path.Combine(mods, "BPModLoaderMod", "Scripts", "main.lua"));
            WriteFile(Path.Combine(mods, "shared", "Types.lua"));
            var ftp = new MemoryFtpClient();
            ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"] = "Existing : 0\r\n; Built-in keybinds, do not move up!\r\nKeybinds : 1\r\n";
            ftp.Files[$"{RemoteUe4ss}/Mods/Existing/Scripts/main.lua"] = "x";
            var vm = await ConnectedAsync(ftp, settings);

            await vm.SyncUe4ssModsToServerCommand.ExecuteAsync(null);

            Assert.Contains($"{RemoteUe4ss}/Mods/OnMod/Scripts/main.lua", ftp.Files.Keys);
            Assert.DoesNotContain(ftp.Files.Keys, f => f.Contains("/OffMod/") || f.Contains("/BPModLoaderMod/") || f.Contains("/shared/") || f.EndsWith(".log", StringComparison.Ordinal));
            var txt = ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"];
            Assert.Equal("Existing : 0\r\nOnMod : 1\r\n; Built-in keybinds, do not move up!\r\nKeybinds : 1\r\n", txt);
            Assert.DoesNotContain(ftp.Files.Keys, f => f.EndsWith(".uploading", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The connection drops on the second mod: the first went up whole and is turned on (it used to
    /// stay off for good, as the next sync skipped its folder as "already there"), the broken one is
    /// named, and nothing claims success.
    /// </summary>
    [Fact]
    public async Task SyncUe4ssMods_ConnectionDropsPartWay_TurnsOnTheModsThatWentUpWholeAndNamesTheBrokenOne()
    {
        var (folder, settings) = GameWithUe4ss();
        try
        {
            var mods = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveModsFolder(settings.Current.IcarusContentPath!);
            WriteFile(Path.Combine(mods, "mods.txt"), "AMod : 1\r\nBMod : 1\r\n");
            WriteFile(Path.Combine(mods, "AMod", "Scripts", "main.lua"));
            WriteFile(Path.Combine(mods, "BMod", "Scripts", "main.lua"));
            var ftp = new MemoryFtpClient { FailUploadContaining = "/BMod/" };
            ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"] = "Keybinds : 1\r\n";
            var vm = await ConnectedAsync(ftp, settings);

            await vm.SyncUe4ssModsToServerCommand.ExecuteAsync(null);

            var txt = ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"];
            Assert.Contains("AMod : 1", txt);
            Assert.DoesNotContain("BMod", txt);
            Assert.Contains("Keybinds : 1", txt);
            Assert.Contains("part way", vm.ConnectionStatusMessage);
            Assert.Contains("'BMod'", vm.ConnectionStatusMessage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A second listing that failed read as "no mods.txt there", and a new one holding only the uploaded mods went up over the server's whole list.</summary>
    [Fact]
    public async Task SyncUe4ssMods_ListingFailsBeforeModsTxtIsEdited_KeepsTheServersOtherLines()
    {
        var (folder, settings) = GameWithUe4ss();
        try
        {
            var mods = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveModsFolder(settings.Current.IcarusContentPath!);
            WriteFile(Path.Combine(mods, "mods.txt"), "OnMod : 1\r\n");
            WriteFile(Path.Combine(mods, "OnMod", "Scripts", "main.lua"));
            var ftp = new MemoryFtpClient();
            ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"] = "Existing : 1\r\nKeybinds : 1\r\n";
            ftp.Files[$"{RemoteUe4ss}/Mods/Existing/Scripts/main.lua"] = "x";
            var vm = await ConnectedAsync(ftp, settings);
            ftp.ListingsBeforeFailure = 1; // the sync's own listing works, any later one fails

            await vm.SyncUe4ssModsToServerCommand.ExecuteAsync(null);

            var txt = ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"];
            Assert.Contains("Existing : 1", txt);
            Assert.Contains("Keybinds : 1", txt);
            Assert.Contains("OnMod : 1", txt);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A host that blocks delete (SurvivalServers): the old merged pak of the SAME name is written
    /// over, so it isn't "left behind". It used to be named in the "will remain" warning and the
    /// result said an old file couldn't be removed and two paks might load.
    /// </summary>
    [Fact]
    public async Task InstallPakToServer_HostBlocksDelete_TheSameNamePakIsNotCalledALeftover()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "ISL-Merged_9999_P.pak"), "pak");
            var ftp = new RecordingFtpClient("ISL-Merged_P.pak");
            var site = MakeSite(Guid.NewGuid());
            site.SupportsDelete = false;
            var dialogs = new FakeDialogService(confirmResult: true);
            var vm = CreateViewModel(new FakeFtpSiteStore([site]), ftp, dialogs, outputPakPath: Path.Combine(folder, "ISL-Merged_9999_P.pak"));
            vm.SelectedSite = vm.Sites.Single();
            vm.PasswordInput = "irrelevant-password";
            await vm.ConnectCommand.ExecuteAsync(null);

            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            Assert.DoesNotContain(dialogs.Messages, m => m.Contains("will remain"));
            Assert.Contains("Installed the merged pak", vm.ConnectionStatusMessage);
            Assert.DoesNotContain("couldn't remove", vm.ConnectionStatusMessage);
            Assert.Contains(Mods + "ISL-Merged_P.pak", ftp.Files);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The game's own UE4SS.log (and crash dumps) used to be uploaded over the server's; the loader files still go.</summary>
    [Fact]
    public async Task SyncUe4ssLoader_SkipsTheGamesLogs()
    {
        var (folder, settings) = GameWithUe4ss();
        try
        {
            var loader = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveLoaderFolder(settings.Current.IcarusContentPath!);
            WriteFile(Path.Combine(loader, "UE4SS.dll"));
            WriteFile(Path.Combine(loader, "UE4SS.log"), "UE4SS - v3.0.1 Beta #0 - Git SHA #abcdef1\r\n");
            WriteFile(Path.Combine(loader, "crash.dmp"));
            WriteFile(IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveDwmapiPath(settings.Current.IcarusContentPath!));
            var ftp = new MemoryFtpClient();
            var vm = await ConnectedAsync(ftp, settings);

            await vm.SyncUe4ssLoaderToServerCommand.ExecuteAsync(null);

            Assert.Contains($"{RemoteUe4ss}/UE4SS.dll", ftp.Files.Keys);
            Assert.Contains("Icarus/Binaries/Win64/dwmapi.dll", ftp.Files.Keys);
            Assert.DoesNotContain($"{RemoteUe4ss}/UE4SS.log", ftp.Files.Keys);
            Assert.DoesNotContain($"{RemoteUe4ss}/crash.dmp", ftp.Files.Keys);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Regression: each loader file was uploaded straight onto its live name, so a connection that
    /// dropped while UE4SS.dll went up left a cut-off UE4SS.dll on the server and the message
    /// never said to sync again before starting it.
    /// </summary>
    [Fact]
    public async Task SyncUe4ssLoader_DropMidUpload_KeepsTheServersOldDll()
    {
        var (folder, settings) = GameWithUe4ss();
        try
        {
            var loader = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveLoaderFolder(settings.Current.IcarusContentPath!);
            WriteFile(Path.Combine(loader, "UE4SS.dll"), "new");
            WriteFile(IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveDwmapiPath(settings.Current.IcarusContentPath!));
            var ftp = new MemoryFtpClient();
            ftp.Files[$"{RemoteUe4ss}/UE4SS.dll"] = "old";
            var vm = await ConnectedAsync(ftp, settings);
            ftp.FailUploadContaining = "UE4SS.dll";

            await vm.SyncUe4ssLoaderToServerCommand.ExecuteAsync(null);

            Assert.Equal("old", ftp.Files[$"{RemoteUe4ss}/UE4SS.dll"]);
            Assert.Contains("again", vm.ConnectionStatusMessage);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Regression: the local copy leaves ArcFission Library's game-session files (state.lua,
    /// registry.txt…) at home, but the FTP sync uploaded them, so a remote server got stale state.
    /// </summary>
    [Fact]
    public async Task SyncUe4ssMods_ArcFissionLibrary_StateFilesStayHome()
    {
        var (folder, settings) = GameWithUe4ss();
        try
        {
            var mods = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveModsFolder(settings.Current.IcarusContentPath!);
            WriteFile(Path.Combine(mods, "mods.txt"), "ArcFissionLibrary : 1\r\n");
            WriteFile(Path.Combine(mods, "ArcFissionLibrary", "Scripts", "main.lua"));
            WriteFile(Path.Combine(mods, "ArcFissionLibrary", "state.lua"), "return { gen = 7 }");
            WriteFile(Path.Combine(mods, "ArcFissionLibrary", "registry.txt"), "WirelessPower|0.9.5");
            var ftp = new MemoryFtpClient();
            ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"] = "Keybinds : 1\r\n";
            var vm = await ConnectedAsync(ftp, settings);

            await vm.SyncUe4ssModsToServerCommand.ExecuteAsync(null);

            Assert.Contains($"{RemoteUe4ss}/Mods/ArcFissionLibrary/Scripts/main.lua", ftp.Files.Keys);
            Assert.DoesNotContain($"{RemoteUe4ss}/Mods/ArcFissionLibrary/state.lua", ftp.Files.Keys);
            Assert.DoesNotContain($"{RemoteUe4ss}/Mods/ArcFissionLibrary/registry.txt", ftp.Files.Keys);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A host that refuses every delete (SurvivalServers) and has never been tested: the second
    /// install of the same pak name still ends with the new pak in place (written over), and the
    /// site is now known to block deletes.
    /// </summary>
    [Fact]
    public async Task InstallPakToServer_DeleteRefused_SecondInstallOfTheSameName_Succeeds()
    {
        var site = MakeSite(Guid.NewGuid());
        var store = new FakeFtpSiteStore([site]);
        var ftp = new RecordingFtpClient("ISL-Merged_P.pak") { RefuseDelete = true };
        var (vm, folder) = await ConnectedWithBuiltPakAsync(ftp, siteStore: store);
        try
        {
            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            Assert.Contains("Installed the merged pak", vm.ConnectionStatusMessage);
            Assert.Contains(Mods + "ISL-Merged_P.pak", ftp.Files);
            Assert.False(store.GetAll().Single().SupportsDelete);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Regression: every file in the site's Mods path was deleted after the upload. A path one
    /// level too high (Icarus/Content/Paks) put the game's own base paks up for deletion behind one
    /// Continue. The game loads mods only from a folder named "mods".
    /// </summary>
    [Fact]
    public async Task InstallPakToServer_ModsPathIsThePaksRoot_DeletesNothing()
    {
        var ftp = new RecordingFtpClient();
        ftp.Files.Add("Icarus/Content/Paks/pakchunk0-WindowsServer.pak");
        var dialogs = new FakeDialogService(confirmResult: true);
        var (vm, folder) = await ConnectedWithBuiltPakAsync(ftp, MakeSite(Guid.NewGuid(), modsPath: "Icarus/Content/Paks"), dialogs);
        try
        {
            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            Assert.DoesNotContain(ftp.Operations, o => o.StartsWith("delete ", StringComparison.Ordinal));
            Assert.Contains("Icarus/Content/Paks/pakchunk0-WindowsServer.pak", ftp.Files);
            Assert.Contains(dialogs.Messages, m => m.Contains("doesn't end in a folder named 'mods'"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Regression: a file in the mods folder that isn't a pak (a readme) was deleted with the old paks.</summary>
    [Fact]
    public async Task InstallPakToServer_NonPakFiles_AreLeftAlone()
    {
        var ftp = new RecordingFtpClient("ISL-Merged_9999_P.pak", "readme.txt");
        var dialogs = new FakeDialogService(confirmResult: true);
        var (vm, folder) = await ConnectedWithBuiltPakAsync(ftp, dialogs: dialogs);
        try
        {
            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            Assert.DoesNotContain(ftp.Operations, o => o == "delete " + Mods + "readme.txt");
            Assert.Contains(Mods + "readme.txt", ftp.Files);
            Assert.DoesNotContain(Mods + "ISL-Merged_9999_P.pak", ftp.Files);
            Assert.Contains("Installed the merged pak", vm.ConnectionStatusMessage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<(ServerViewModel Vm, FakeDialogService Dialogs, MemoryFtpClient Ftp, string Folder)> BrowsingWithLocalFileAsync(bool confirm, string text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, "MyWorld.json");
        File.WriteAllText(local, text);
        var ftp = new MemoryFtpClient();
        ftp.Files["/MyWorld.json"] = "server copy";
        var dialogs = new FakeDialogService(confirm) { OpenFilePath = local };
        var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), ftp, dialogs);
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);
        return (vm, dialogs, ftp, folder);
    }

    /// <summary>Regression: Upload wrote a picked file over a server file of the same name (a live world) without asking.</summary>
    [Fact]
    public async Task Upload_ExistingName_AsksFirst()
    {
        var (vm, dialogs, ftp, folder) = await BrowsingWithLocalFileAsync(confirm: false, "new");
        try
        {
            await vm.UploadCommand.ExecuteAsync(null);

            Assert.Equal(1, dialogs.ConfirmCallCount);
            Assert.Equal("server copy", ftp.Files["/MyWorld.json"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Regression: a replacing upload went straight onto the live name, so a dropped connection left the server's file cut off.</summary>
    [Fact]
    public async Task Upload_Replacing_GoesUpUnderATempNameFirst()
    {
        var (vm, _, ftp, folder) = await BrowsingWithLocalFileAsync(confirm: true, "new");
        try
        {
            ftp.FailUploadContaining = "MyWorld.json";

            await vm.UploadCommand.ExecuteAsync(null);

            Assert.Equal("server copy", ftp.Files["/MyWorld.json"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Saying yes replaces the server's file, and no temporary copy stays behind.</summary>
    [Fact]
    public async Task Upload_ReplaceConfirmed_EndsWithYourFileAndNoTempCopy()
    {
        var (vm, _, ftp, folder) = await BrowsingWithLocalFileAsync(confirm: true, "new");
        try
        {
            await vm.UploadCommand.ExecuteAsync(null);

            Assert.Equal("new", ftp.Files["/MyWorld.json"]);
            Assert.DoesNotContain(ftp.Files.Keys, f => f.EndsWith(".uploading", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A game with UE4SS whose loader folder holds UE4SS.dll and a UE4SS-settings.ini reading "game settings".</summary>
    private static (string Folder, FakeSettingsService Settings) GameWithUe4ssAndSettings()
    {
        var (folder, settings) = GameWithUe4ss();
        var loader = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveLoaderFolder(settings.Current.IcarusContentPath!);
        WriteFile(Path.Combine(loader, "UE4SS.dll"));
        WriteFile(Path.Combine(loader, Ue4ssServerFiles.SettingsFileName), "game settings");
        WriteFile(IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveDwmapiPath(settings.Current.IcarusContentPath!));
        return (folder, settings);
    }

    /// <summary>
    /// Regression: a listing of the server's ue4ss folder that failed (a slow host timing out)
    /// read as "the server has no UE4SS-settings.ini", and the game's went up over the server's.
    /// </summary>
    [Fact]
    public async Task SyncUe4ssLoader_ListingFails_DoesNotSendTheSettingsFile()
    {
        var (folder, settings) = GameWithUe4ssAndSettings();
        try
        {
            var ftp = new MemoryFtpClient();
            ftp.Files[$"{RemoteUe4ss}/{Ue4ssServerFiles.SettingsFileName}"] = "server settings";
            var dialogs = new FakeDialogService(confirmResult: true);
            var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), ftp, dialogs, settings: settings);
            vm.SelectedSite = vm.Sites.Single();
            vm.PasswordInput = "irrelevant-password";
            await vm.ConnectCommand.ExecuteAsync(null);
            ftp.ListingsBeforeFailure = 0;

            await vm.SyncUe4ssLoaderToServerCommand.ExecuteAsync(null);

            Assert.Equal("server settings", ftp.Files[$"{RemoteUe4ss}/{Ue4ssServerFiles.SettingsFileName}"]);
            Assert.Contains($"{RemoteUe4ss}/UE4SS.dll", ftp.Files.Keys);
            Assert.Contains(dialogs.Messages, m => m.Contains("Couldn't check whether the server has its own UE4SS-settings.ini"));
            Assert.Contains("Couldn't check", vm.ConnectionStatusMessage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A fresh server (Win64 there, no ue4ss folder yet) still gets the game's UE4SS-settings.ini.</summary>
    [Fact]
    public async Task SyncUe4ssLoader_FreshServer_GetsTheSettingsFile()
    {
        var (folder, settings) = GameWithUe4ssAndSettings();
        try
        {
            var ftp = new MemoryFtpClient();
            ftp.Files["Icarus/Binaries/Win64/IcarusServer-Win64-Shipping.exe"] = "server";
            var vm = await ConnectedAsync(ftp, settings);

            await vm.SyncUe4ssLoaderToServerCommand.ExecuteAsync(null);

            Assert.Equal("game settings", ftp.Files[$"{RemoteUe4ss}/{Ue4ssServerFiles.SettingsFileName}"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A server that has its own UE4SS-settings.ini keeps it.</summary>
    [Fact]
    public async Task SyncUe4ssLoader_ServerHasItsOwnSettings_KeepsThem()
    {
        var (folder, settings) = GameWithUe4ssAndSettings();
        try
        {
            var ftp = new MemoryFtpClient();
            ftp.Files[$"{RemoteUe4ss}/UE4SS.dll"] = "old";
            ftp.Files[$"{RemoteUe4ss}/{Ue4ssServerFiles.SettingsFileName}"] = "server settings";
            var vm = await ConnectedAsync(ftp, settings);

            await vm.SyncUe4ssLoaderToServerCommand.ExecuteAsync(null);

            Assert.Equal("server settings", ftp.Files[$"{RemoteUe4ss}/{Ue4ssServerFiles.SettingsFileName}"]);
            Assert.Equal("x", ftp.Files[$"{RemoteUe4ss}/UE4SS.dll"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Regression: a typed password was saved before connecting, so a mistyped one replaced the working saved password even when the login failed.</summary>
    [Fact]
    public async Task ConnectAsync_LoginFails_KeepsTheSavedPassword()
    {
        var site = MakeSite(Guid.NewGuid());
        var credentials = new RecordingCredentialStore();
        credentials.Saved[CredentialTargets.FtpSite(site.Id)] = "right";
        var vm = CreateViewModel(new FakeFtpSiteStore([site]), new FakeFtpClient { ConnectFailure = new InvalidOperationException("530 Login incorrect.") }, credentialStore: credentials);
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "wrong";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.Empty(credentials.SaveCalls);
        Assert.Equal("right", credentials.Saved[CredentialTargets.FtpSite(site.Id)]);
        Assert.Equal("wrong", vm.PasswordInput);
    }

    /// <summary>A login that works saves the typed password, once, and clears the box.</summary>
    [Fact]
    public async Task ConnectAsync_LoginWorks_SavesTheTypedPasswordOnce()
    {
        var site = MakeSite(Guid.NewGuid());
        var credentials = new RecordingCredentialStore();
        var vm = CreateViewModel(new FakeFtpSiteStore([site]), new FakeFtpClient(), credentialStore: credentials);
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "typed";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.Equal(["typed"], credentials.SaveCalls);
        Assert.Equal("", vm.PasswordInput);
    }

    /// <summary>Regression: a Credential Manager that couldn't save a typed password stopped Connect before it tried; the login works now and the page says the password wasn't kept.</summary>
    [Fact]
    public async Task ConnectAsync_TypedPassword_CredentialManagerFails_StillConnectsAndSaysSo()
    {
        var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), new FakeFtpClient(), credentialStore: new ThrowingCredentialStore());
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "typed";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.Contains("couldn't be saved in Windows Credential Manager", vm.ConnectionStatusMessage);
    }

    /// <summary>The Encryption box shows every mode in plain words, and the unencrypted one says the password travels as plain text.</summary>
    [Fact]
    public void EncryptionChoices_CoverEveryMode_InPlainWords()
    {
        Assert.Equal(
            Enum.GetValues<FtpEncryptionMode>().Order(),
            ServerViewModel.EncryptionChoices.Select(c => c.Mode).Order());
        Assert.Contains("plain text", ServerViewModel.EncryptionChoices.Single(c => c.Mode == FtpEncryptionMode.None).Label);
        Assert.Contains("recommended", ServerViewModel.EncryptionChoices.Single(c => c.Mode == FtpEncryptionMode.Auto).Label);
    }

    /// <summary>Regression: a new site started as plain FTP, so the password and every upload travelled unencrypted unless the user changed the box.</summary>
    [Fact]
    public void NewSite_StartsEncryptedWhenTheHostSupportsIt()
    {
        var vm = CreateViewModel(new FakeFtpSiteStore([]), new FakeFtpClient());

        vm.NewSiteCommand.Execute(null);

        Assert.Equal(FtpEncryptionMode.Auto, vm.EncryptionModeInput);
    }

    /// <summary>A saved site keeps the encryption it was saved with.</summary>
    [Fact]
    public void SelectingASavedPlainSite_KeepsItsSetting()
    {
        var site = MakeSite(Guid.NewGuid());
        site.EncryptionMode = FtpEncryptionMode.None;
        var vm = CreateViewModel(new FakeFtpSiteStore([site]), new FakeFtpClient());
        vm.NewSiteCommand.Execute(null);

        vm.SelectedSite = vm.Sites.Single();

        Assert.Equal(FtpEncryptionMode.None, vm.EncryptionModeInput);
    }

    /// <summary>After connecting, the page says whether the connection is encrypted (it showed nothing once the folder was listed).</summary>
    [Theory]
    [InlineData(false, "isn't encrypted")]
    [InlineData(true, "is encrypted")]
    public async Task ConnectAsync_SaysWhetherTheConnectionIsEncrypted(bool encrypted, string expected)
    {
        var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), new FakeFtpClient { IsEncrypted = encrypted });
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Contains(expected, vm.ConnectionStatusMessage);
    }

    /// <summary>A game with UE4SS mods.txt "OnMod : 1" and OnMod's main.lua, and a server with Keybinds on.</summary>
    private static (string Folder, FakeSettingsService Settings, MemoryFtpClient Ftp) GameWithOnModAndServer()
    {
        var (folder, settings) = GameWithUe4ss();
        var mods = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveModsFolder(settings.Current.IcarusContentPath!);
        WriteFile(Path.Combine(mods, "mods.txt"), "OnMod : 1\r\n");
        WriteFile(Path.Combine(mods, "OnMod", "Scripts", "main.lua"));
        var ftp = new MemoryFtpClient();
        ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"] = "Keybinds : 1\r\n";
        return (folder, settings, ftp);
    }

    /// <summary>
    /// Regression: when the mods.txt step failed (a dropped connection), the page said only "UE4SS
    /// mod sync failed", not which mods were now on the server but not turned on.
    /// </summary>
    [Fact]
    public async Task SyncUe4ssMods_ModsTxtUploadFails_NamesTheModsLeftOff()
    {
        var (folder, settings, ftp) = GameWithOnModAndServer();
        try
        {
            var vm = await ConnectedAsync(ftp, settings);
            ftp.FailUploadContaining = "mods.txt";

            await vm.SyncUe4ssModsToServerCommand.ExecuteAsync(null);

            Assert.Contains($"{RemoteUe4ss}/Mods/OnMod/Scripts/main.lua", ftp.Files.Keys);
            Assert.Contains("OnMod", vm.ConnectionStatusMessage);
            Assert.Contains("not turned on", vm.ConnectionStatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Regression: a mod whose folder went up but whose mods.txt step failed stayed off for good:
    /// the next sync saw its folder, said "already has every mod" and never turned it on.
    /// </summary>
    [Fact]
    public async Task SyncUe4ssMods_AfterTheModsTxtStepFailed_NextSyncTurnsTheModOn()
    {
        var (folder, settings, ftp) = GameWithOnModAndServer();
        try
        {
            var dialogs = new FakeDialogService(confirmResult: true);
            var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), ftp, dialogs, settings: settings);
            vm.SelectedSite = vm.Sites.Single();
            vm.PasswordInput = "irrelevant-password";
            await vm.ConnectCommand.ExecuteAsync(null);
            ftp.FailUploadContaining = "mods.txt";
            await vm.SyncUe4ssModsToServerCommand.ExecuteAsync(null);
            ftp.FailUploadContaining = null;

            await vm.SyncUe4ssModsToServerCommand.ExecuteAsync(null);

            var txt = ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"];
            Assert.Contains("OnMod : 1", txt);
            Assert.Contains("Keybinds : 1", txt);
            Assert.DoesNotContain("already has every", vm.ConnectionStatusMessage);
            Assert.Contains("OnMod", dialogs.Messages[^1]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A mod the server's admin turned off ("OnMod : 0") stays off, and one that starts by its enabled.txt is left alone.</summary>
    [Fact]
    public async Task SyncUe4ssMods_ModOffOnTheServerOrStartedByEnabledTxt_IsLeftAsTheServerHasIt()
    {
        var (folder, settings) = GameWithUe4ss();
        try
        {
            var mods = IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveModsFolder(settings.Current.IcarusContentPath!);
            WriteFile(Path.Combine(mods, "mods.txt"), "OffThere : 1\r\nByFile : 1\r\n");
            WriteFile(Path.Combine(mods, "OffThere", "Scripts", "main.lua"));
            WriteFile(Path.Combine(mods, "ByFile", "Scripts", "main.lua"));
            var ftp = new MemoryFtpClient();
            ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"] = "OffThere : 0\r\nKeybinds : 1\r\n";
            ftp.Files[$"{RemoteUe4ss}/Mods/OffThere/Scripts/main.lua"] = "x";
            ftp.Files[$"{RemoteUe4ss}/Mods/ByFile/Scripts/main.lua"] = "x";
            ftp.Files[$"{RemoteUe4ss}/Mods/ByFile/enabled.txt"] = "";
            var dialogs = new FakeDialogService(confirmResult: true);
            var vm = CreateViewModel(new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]), ftp, dialogs, settings: settings);
            vm.SelectedSite = vm.Sites.Single();
            vm.PasswordInput = "irrelevant-password";
            await vm.ConnectCommand.ExecuteAsync(null);

            await vm.SyncUe4ssModsToServerCommand.ExecuteAsync(null);

            Assert.Equal("OffThere : 0\r\nKeybinds : 1\r\n", ftp.Files[$"{RemoteUe4ss}/Mods/mods.txt"]);
            Assert.Equal(0, dialogs.ConfirmCallCount);
            Assert.Contains("already has every", vm.ConnectionStatusMessage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Regression: when ftp_sites.json couldn't be saved (held by a scanner), Install merged pak
    /// stopped between deleting the old paks and renaming the new one into place, leaving the
    /// server with only "….pak.uploading", which the game doesn't load.
    /// </summary>
    [Fact]
    public async Task InstallPakToServer_SiteListCantBeSaved_StillRenamesThePakIntoPlace()
    {
        var siteStore = new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]);
        var ftp = new RecordingFtpClient("IMM_Merged_Mod_P.pak");
        var (vm, folder) = await ConnectedWithBuiltPakAsync(ftp, siteStore: siteStore);
        try
        {
            siteStore.ThrowOnSave = true;

            await vm.InstallPakToServerCommand.ExecuteAsync(null);

            Assert.Contains($"rename {Mods}ISL-Merged_P.pak.uploading -> {Mods}ISL-Merged_P.pak", ftp.Operations);
            Assert.Contains("Installed the merged pak", vm.ConnectionStatusMessage);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Regression: a delete that worked was reported as "Delete failed" when ftp_sites.json couldn't be saved afterwards.</summary>
    [Fact]
    public async Task DeleteRemote_SiteListCantBeSaved_StillSaysDeleted()
    {
        var siteStore = new FakeFtpSiteStore([MakeSite(Guid.NewGuid())]);
        var vm = CreateViewModel(siteStore, new DeletingFtpClient());
        vm.SelectedSite = vm.Sites.Single();
        vm.PasswordInput = "irrelevant-password";
        await vm.ConnectCommand.ExecuteAsync(null);
        siteStore.ThrowOnSave = true;
        vm.SelectedRemoteEntry = new FtpEntry("some-file.txt", false, 10, null);

        await vm.DeleteRemoteCommand.ExecuteAsync(null);

        Assert.Equal("Deleted 'some-file.txt'.", vm.ConnectionStatusMessage);
    }

    /// <summary>An FTP server in memory with real folders and file contents: listing shows the direct children.</summary>
    private sealed class MemoryFtpClient : IFtpClient
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool IsEncrypted => false;

        /// <summary>Listings that still work; after that every listing fails (a dropped connection). Null: never fails.</summary>
        public int? ListingsBeforeFailure { get; set; }

        /// <summary>An upload whose remote path contains this fails part way (the file is left half there).</summary>
        public string? FailUploadContaining { get; set; }

        public Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            if (ListingsBeforeFailure is { } left)
            {
                if (left <= 0)
                {
                    throw new IOException("Connection reset.");
                }

                ListingsBeforeFailure = left - 1;
            }

            var prefix = remotePath.TrimEnd('/') + "/";
            var children = Files.Keys.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(f => f[prefix.Length..])
                .Select(rest => rest.Contains('/') ? new FtpEntry(rest[..rest.IndexOf('/')], true, null, null) : new FtpEntry(rest, false, 1, null))
                .DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<FtpEntry>>(children);
        }

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
        {
            if (FailUploadContaining is { } failing && remotePath.Contains(failing, StringComparison.OrdinalIgnoreCase))
            {
                Files[remotePath] = "partial";
                throw new IOException("Connection reset mid-upload.");
            }

            Files[remotePath] = File.ReadAllText(localPath);
            return Task.CompletedTask;
        }

        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
        {
            if (!Files.TryGetValue(remotePath, out var text))
            {
                throw new IOException("550 File not found");
            }

            File.WriteAllText(localPath, text);
            return Task.CompletedTask;
        }

        public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            Files.Remove(remotePath);
            return Task.CompletedTask;
        }

        public Task RenameFileAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken = default)
        {
            Files[toRemotePath] = Files[fromRemotePath];
            Files.Remove(fromRemotePath);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingCredentialStore : ICredentialStore
    {
        public void Save(string target, string secret) => throw new InvalidOperationException("Credential Manager is unavailable");

        public string? Read(string target) => throw new InvalidOperationException("Credential Manager is unavailable");

        public void Delete(string target) { }
    }

    /// <summary>Holds one saved password per target and records every Save.</summary>
    private sealed class RecordingCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Saved { get; } = [];

        public List<string> SaveCalls { get; } = [];

        public void Save(string target, string secret)
        {
            SaveCalls.Add(secret);
            Saved[target] = secret;
        }

        public string? Read(string target) => Saved.TryGetValue(target, out var secret) ? secret : null;

        public void Delete(string target) => Saved.Remove(target);
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

        public List<string> Messages { get; } = [];

        /// <summary>What the file picker answers; null: cancelled.</summary>
        public string? OpenFilePath { get; set; }

        public string? PickFile(string title, string filter, string? initialDirectory = null) => OpenFilePath;

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            ConfirmCallCount++;
            Messages.Add(message);
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
