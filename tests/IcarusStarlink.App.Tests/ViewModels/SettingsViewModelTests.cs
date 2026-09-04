using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Catalog.Ue4ss;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Skins;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// SettingsViewModel had zero coverage before this file despite owning several safety/data-
/// integrity flows — this covers, deliberately, only the three highest-risk ones rather than
/// chasing every method on a ~1400-line class:
///
/// 1. UpdateDataFolderAsync (UpdateDataFolderCommand) — the extraction + Weekly Changes report
///    flow: this app's own headline feature this session, previously verified only by hand
///    against a real game update, never at the ViewModel layer.
/// 2. VerifyUnrealPakAsync/InstallBundledUnrealPakAsync — the VIEWMODEL's own orchestration
///    (status messages, button-label state) of IUnrealPakInstaller, which already has its own
///    real tests (UnrealPakInstallerTests) for the installer's own internals — not re-tested here.
/// 3. InstallOrUpdateUe4ssAsync/UninstallUe4ssAsync — picked as the single riskiest, most complex,
///    completely-untested method in this class: its own doc comment calls this write "more
///    sensitive than either target this app has written to before" (a real write into the game's
///    own Binaries\Win64, replacing its loader), and unlike the UnrealPak flow above, it also
///    layers on a real HTTP download and zip handling before that write happens.
///
/// SettingsViewModel had NO IDialogService seam before this file — every one of its six
/// confirmations (first-launch UnrealPak setup, nxm:// registration, UE4SS install/update/
/// uninstall, and both app-update confirms) called ThemedMessageBox.Show directly, the same
/// real-Window-and-blocking-ShowDialog() call SavesViewModelTests' own doc comment describes as
/// having no live Dispatcher/Application to run against in a test host. Migrating all six to
/// _dialogService.Confirm (see SettingsViewModel's own diff) is what makes InstallOrUpdateUe4ss/
/// UninstallUe4ss testable at all here, mirroring SavesViewModel's own migration exactly — same
/// FakeDialogService shape, copied from SavesViewModelTests.
///
/// A second, harder-to-remove risk this class alone has (SavesViewModel's constructor does no such
/// thing): the constructor itself fires five fire-and-forget async checks
/// (CheckForGameUpdateAsync/InitializeNexusStatusAsync/CheckUe4ssLatestReleaseAsync/
/// CheckForAppUpdatesOnLaunchAsync/EnsureUnrealPakOnLaunchAsync), unawaited, with no seam like
/// SavesViewModel's own WaitForPendingSlotLoadAsync to await them from a test. Harness's own
/// default fakes are deliberately shaped so every one of these returns/no-ops synchronously (a
/// non-null, already-on-disk UnrealPakExePath so EnsureUnrealPakOnLaunchAsync's very first check
/// short-circuits before any await; null LastDataPakHash/stored Nexus key so the other two return
/// at their own first line; Task.FromResult(...) — never Task.Run/Task.Delay — everywhere else) so
/// they complete before the constructor call returns in a normal xunit host (no ambient
/// SynchronizationContext forces a real thread hop for an already-completed await). This is an
/// inherent property of the class today, not something introduced by these tests — see this file's
/// own final report for why EnsureUnrealPakOnLaunchAsync's own dialog-driven first-launch-install
/// path specifically is deliberately NOT covered here.
/// </summary>
public sealed class SettingsViewModelTests
{
    private static WeeklyChangeReport MakeReport(int changedFileCount) => new(
        DateTimeOffset.UtcNow.AddDays(-7),
        DateTimeOffset.UtcNow,
        Enumerable.Range(0, changedFileCount)
            .Select(i => new ChangedDataFile($"Crafting/D_Fuel{i}.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: [], FieldChanges: []))
            .ToList());

    private static HttpMessageHandler SucceedingHandler(byte[] bytes) =>
        new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

    // =====================================================================================
    // 1. UpdateDataFolderAsync — extraction + Weekly Changes report generation
    // =====================================================================================

    [Fact]
    public async Task UpdateDataFolderCommand_MissingPaths_ShowsGuidance_NeverExtracts()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        vm.IcarusContentPath = null;

        await vm.UpdateDataFolderCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.UnrealPakService.ExtractCallCount);
        Assert.Contains("Set both", vm.DataFolderStatusMessage);
    }

    [Fact]
    public async Task UpdateDataFolderCommand_Success_WithChanges_SavesReportPersistsHashAndBroadcasts()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        vm.GameDataOutdatedMessage = "stale from a previous check";
        var report = MakeReport(changedFileCount: 3);
        harness.UnrealPakService.ExtractResultFactory = () => new UnrealPakExtractResult(42, report);
        harness.UnrealPakService.HashToReturn = "hash-after-update";

        var broadcastCount = 0;
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (_, _) => broadcastCount++);
        try
        {
            await vm.UpdateDataFolderCommand.ExecuteAsync(null);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<WeeklyChangeReportUpdatedMessage>(this);
        }

        Assert.Equal(1, harness.WeeklyChangeReportStore.SaveCallCount);
        Assert.Same(report, harness.WeeklyChangeReportStore.LastSavedReport);
        Assert.Equal("hash-after-update", harness.Settings.LastDataPakHash);
        Assert.NotNull(harness.Settings.LastDataFolderUpdatedAt);
        Assert.Null(vm.GameDataOutdatedMessage);
        Assert.True(vm.HasWeeklyChanges);
        Assert.Contains("Extracted 42 files", vm.DataFolderStatusMessage);
        Assert.Contains("3 JSON file(s) changed", vm.DataFolderStatusMessage);
        Assert.Equal(1, broadcastCount);
        Assert.False(vm.IsUpdatingDataFolder);
        Assert.Contains("last updated", vm.DataFolderSummary);
    }

    [Fact]
    public async Task UpdateDataFolderCommand_Success_FirstRun_NoChangeReport_SkipsReportStoreSave()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        harness.UnrealPakService.ExtractResultFactory = () => new UnrealPakExtractResult(10, null);

        await vm.UpdateDataFolderCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.WeeklyChangeReportStore.SaveCallCount);
        Assert.False(vm.HasWeeklyChanges);
        Assert.Contains("first update", vm.DataFolderStatusMessage);
        Assert.NotNull(harness.Settings.LastDataFolderUpdatedAt);
    }

    [Fact]
    public async Task UpdateDataFolderCommand_ExtractThrows_UnrealPakHealthy_ShowsRawExceptionMessage()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        harness.UnrealPakService.ExtractException = new InvalidOperationException("disk full");
        harness.UnrealPakInstaller.VerifyResult = new UnrealPakVerifyResult(UnrealPakHealth.Ok, "4.27.0", null);

        await vm.UpdateDataFolderCommand.ExecuteAsync(null);

        Assert.Equal("Update failed: disk full", vm.DataFolderStatusMessage);
        Assert.Equal(0, harness.WeeklyChangeReportStore.SaveCallCount);
        Assert.False(vm.IsUpdatingDataFolder);
        Assert.Null(harness.Settings.LastDataFolderUpdatedAt);
    }

    [Fact]
    public async Task UpdateDataFolderCommand_ExtractThrows_UnrealPakBroken_PointsAtReinstall()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        harness.UnrealPakService.ExtractException = new InvalidOperationException("boom");
        harness.UnrealPakInstaller.VerifyResult = new UnrealPakVerifyResult(UnrealPakHealth.Broken, null, "missing DLL");

        await vm.UpdateDataFolderCommand.ExecuteAsync(null);

        Assert.Contains("your UnrealPak.exe copy looks broken", vm.DataFolderStatusMessage);
        Assert.Contains("Reinstall", vm.DataFolderStatusMessage);
    }

    // =====================================================================================
    // 2. VerifyUnrealPakAsync / InstallBundledUnrealPakAsync — the VIEWMODEL's own
    //    orchestration of IUnrealPakInstaller (status messages, button-label state); the
    //    installer's own internals are covered separately by UnrealPakInstallerTests.
    // =====================================================================================

    [Fact]
    public async Task VerifyUnrealPakCommand_NoPathSet_ShowsGuidance_NeverCallsInstaller()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        vm.UnrealPakExePath = null;

        await vm.VerifyUnrealPakCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.UnrealPakInstaller.VerifyCallCount);
        Assert.Contains("No UnrealPak.exe set", vm.UnrealPakStatusMessage);
    }

    public static IEnumerable<object[]> VerifyHealthCases()
    {
        yield return [new UnrealPakVerifyResult(UnrealPakHealth.Ok, "4.27.0", null), "UE4, matches Icarus"];
        yield return [new UnrealPakVerifyResult(UnrealPakHealth.Ok, "5.1.0", null), "WARNING: not a UE4 build"];
        yield return [new UnrealPakVerifyResult(UnrealPakHealth.Missing, null, "no file there"), "Not found at that path"];
        yield return [new UnrealPakVerifyResult(UnrealPakHealth.Broken, null, "bad dll"), "Broken copy"];
    }

    [Theory]
    [MemberData(nameof(VerifyHealthCases))]
    public async Task VerifyUnrealPakCommand_ReportsHealthAccordingly(UnrealPakVerifyResult result, string expectedSubstring)
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        harness.UnrealPakInstaller.VerifyResult = result;

        await vm.VerifyUnrealPakCommand.ExecuteAsync(null);

        Assert.Contains(expectedSubstring, vm.UnrealPakStatusMessage);
        Assert.Equal(1, harness.UnrealPakInstaller.VerifyCallCount);
        Assert.Equal(harness.ValidUnrealPakExePath, harness.UnrealPakInstaller.LastVerifiedExePath);
    }

    [Fact]
    public async Task InstallBundledUnrealPakCommand_Success_SetsPathSavesSettingsAndVerifies()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        var installedPath = harness.UnrealPakInstaller.InstalledExePath;
        harness.UnrealPakInstaller.InstallResultPath = installedPath;
        harness.UnrealPakInstaller.VerifyResult = new UnrealPakVerifyResult(UnrealPakHealth.Ok, "4.27.0", null);
        var saveCallsBefore = harness.SettingsService.SaveCallCount;

        await vm.InstallBundledUnrealPakCommand.ExecuteAsync(null);

        Assert.Equal(installedPath, vm.UnrealPakExePath);
        Assert.True(harness.SettingsService.SaveCallCount > saveCallsBefore);
        Assert.Contains("Working — UnrealPak 4.27.0", vm.UnrealPakStatusMessage);
        Assert.Equal("Reinstall UnrealPak", vm.InstallUnrealPakButtonLabel);
        Assert.Equal(1, harness.UnrealPakInstaller.InstallCallCount);
        Assert.Equal(1, harness.UnrealPakInstaller.VerifyCallCount);
    }

    [Fact]
    public async Task InstallBundledUnrealPakCommand_InstallThrows_ShowsInstallFailedMessage_PathUnchanged()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        var originalPath = vm.UnrealPakExePath;
        harness.UnrealPakInstaller.InstallException = new IOException("access denied");

        await vm.InstallBundledUnrealPakCommand.ExecuteAsync(null);

        Assert.Equal("Install failed: access denied", vm.UnrealPakStatusMessage);
        Assert.Equal(originalPath, vm.UnrealPakExePath);
        Assert.Equal(0, harness.UnrealPakInstaller.VerifyCallCount);
    }

    // =====================================================================================
    // 3. InstallOrUpdateUe4ssAsync / UninstallUe4ssAsync — the riskiest, most complex,
    //    completely-untested method in this class before this file: its own doc comment calls
    //    this write "more sensitive than either target this app has written to before" (a real
    //    write into the game's own Binaries\Win64, replacing its loader), and it layers a real
    //    HTTP download + zip handoff on top of that write, unlike UnrealPak's own local-only
    //    install. Both commands' own confirm dialogs are what required the IDialogService
    //    migration in the first place.
    // =====================================================================================

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_NoLatestReleaseAvailable_ShowsGuidance_NeverConfirms()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Contains("Couldn't reach GitHub", vm.Ue4ssStatusMessage);
        Assert.Equal(0, harness.DialogService.ConfirmCallCount);
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_Declined_DoesNotDownloadOrInstall()
    {
        using var harness = new Harness(dialogConfirmResult: false);
        harness.Ue4ssReleaseClient.ReleaseToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip");
        var vm = harness.BuildViewModel();

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_Confirmed_DownloadsInstallsAndRefreshesStatus()
    {
        var payloadBytes = new byte[] { 1, 2, 3, 4 };
        using var harness = new Harness(dialogConfirmResult: true, httpHandler: SucceedingHandler(payloadBytes));
        harness.Ue4ssReleaseClient.ReleaseToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip");
        harness.Ue4ssLoaderInstallService.StatusAfterInstall = new Ue4ssLoaderStatus(true, "3.0.1");
        var vm = harness.BuildViewModel();

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.Equal(vm.IcarusContentPath, harness.Ue4ssLoaderInstallService.LastIcarusContentPath);
        Assert.Contains("UE4SS_", harness.Ue4ssLoaderInstallService.LastDownloadedZipPath);
        Assert.False(File.Exists(harness.Ue4ssLoaderInstallService.LastDownloadedZipPath));
        Assert.True(vm.IsUe4ssInstalled);
        Assert.Equal("3.0.1", vm.Ue4ssInstalledVersion);
        Assert.Equal("Installed to v3.0.1.", vm.Ue4ssStatusMessage);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_DownloadFails_ShowsInstallFailedMessage()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        harness.Ue4ssReleaseClient.ReleaseToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip");
        var vm = harness.BuildViewModel();

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Contains("Install failed:", vm.Ue4ssStatusMessage);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_ServiceThrows_WhenAlreadyInstalled_ShowsUpdateFailedMessage()
    {
        var payloadBytes = new byte[] { 9, 9 };
        using var harness = new Harness(dialogConfirmResult: true, httpHandler: SucceedingHandler(payloadBytes));
        harness.Ue4ssReleaseClient.ReleaseToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip");
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "2.9.0");
        harness.Ue4ssLoaderInstallService.InstallOrUpdateException = new IOException("locked file");
        var vm = harness.BuildViewModel();
        Assert.True(vm.IsUe4ssInstalled);

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal("Update failed: locked file", vm.Ue4ssStatusMessage);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task UninstallUe4ssCommand_NotInstalled_ShowsNothingToRemove_NeverConfirms()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();

        await vm.UninstallUe4ssCommand.ExecuteAsync(null);

        Assert.Contains("nothing to remove", vm.Ue4ssStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.DialogService.ConfirmCallCount);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.UninstallCallCount);
    }

    [Fact]
    public async Task UninstallUe4ssCommand_Declined_DoesNotUninstall()
    {
        using var harness = new Harness(dialogConfirmResult: false);
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1");
        var vm = harness.BuildViewModel();

        await vm.UninstallUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.UninstallCallCount);
        Assert.True(vm.IsUe4ssInstalled);
    }

    [Fact]
    public async Task UninstallUe4ssCommand_Confirmed_ReportsPreservedModsAndRefreshesStatus()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1");
        harness.Ue4ssLoaderInstallService.UserAddedMods = ["MyCoolMod", "AnotherMod"];
        harness.Ue4ssLoaderInstallService.UninstallResult = new Ue4ssUninstallResult(["MyCoolMod", "AnotherMod"], "backup.zip");
        harness.Ue4ssLoaderInstallService.StatusAfterUninstall = new Ue4ssLoaderStatus(false, null);
        var vm = harness.BuildViewModel();

        await vm.UninstallUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Ue4ssLoaderInstallService.UninstallCallCount);
        Assert.Contains("Your 2 mod(s) were kept", vm.Ue4ssStatusMessage);
        Assert.False(vm.IsUe4ssInstalled);
        // The confirm dialog itself names the preserved mods before anything happens.
        Assert.Contains("MyCoolMod", harness.DialogService.LastConfirmMessage);
        Assert.Contains("AnotherMod", harness.DialogService.LastConfirmMessage);
    }

    [Fact]
    public async Task UninstallUe4ssCommand_ServiceThrows_ShowsUninstallFailedMessage()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1");
        harness.Ue4ssLoaderInstallService.UninstallException = new IOException("in use");
        var vm = harness.BuildViewModel();

        await vm.UninstallUe4ssCommand.ExecuteAsync(null);

        Assert.Equal("Uninstall failed: in use", vm.Ue4ssStatusMessage);
        Assert.True(vm.IsUe4ssInstalled);
    }

    // =====================================================================================
    // Harness — builds a real SettingsViewModel with a fake per dependency (this codebase's own
    // established convention — see SavesViewModelTests). Defaults are chosen so the constructor's
    // own five fire-and-forget launch checks all resolve harmlessly (see this file's own top doc
    // comment) without needing to touch any of them from a given test.
    // =====================================================================================

    private sealed class Harness : IDisposable
    {
        public readonly string TempDir;
        public readonly string ValidUnrealPakExePath;
        public readonly AppSettings Settings = new();
        public readonly FakeSettingsService SettingsService;
        public readonly FakeUnrealPakService UnrealPakService = new();
        public readonly FakeWeeklyChangeReportStore WeeklyChangeReportStore = new();
        public readonly FakeUnrealPakInstaller UnrealPakInstaller = new();
        public readonly FakeUe4ssLoaderInstallService Ue4ssLoaderInstallService = new();
        public readonly FakeUe4ssReleaseClient Ue4ssReleaseClient = new();
        public readonly FakeAppUpdateClient AppUpdateClient = new();
        public readonly FakeDialogService DialogService;
        public readonly FakeActivityLog ActivityLog = new();
        private readonly HttpMessageHandler _httpHandler;

        public Harness(bool dialogConfirmResult = true, HttpMessageHandler? httpHandler = null)
        {
            TempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_Settings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);

            // A real, already-on-disk path — so EnsureUnrealPakOnLaunchAsync's own very first
            // check (UnrealPakExePath set AND File.Exists) short-circuits before any await, and
            // the constructor's fire-and-forget call never reaches its own confirm dialog.
            ValidUnrealPakExePath = Path.Combine(TempDir, "UnrealPak.exe");
            File.WriteAllText(ValidUnrealPakExePath, "stand-in");

            Settings.IcarusContentPath = Path.Combine(TempDir, "IcarusContent");
            Settings.UnrealPakExePath = ValidUnrealPakExePath;
            SettingsService = new FakeSettingsService(Settings);
            DialogService = new FakeDialogService(dialogConfirmResult);
            _httpHandler = httpHandler ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public SettingsViewModel BuildViewModel() => new(
            SettingsService, UnrealPakService, WeeklyChangeReportStore,
            new FakeSteamInstallLocator(), new FakeCredentialStore(), new FakeNexusApiClient(),
            new FakeNxmProtocolRegistrar(), Ue4ssLoaderInstallService, Ue4ssReleaseClient, AppUpdateClient,
            new HttpClient(_httpHandler), new FakeThemeService(), new FakeCustomSkinStore(),
            BuildImmMigrationService(),
            () => throw new InvalidOperationException("MergeInstallViewModel factory not exercised by these tests"),
            UnrealPakInstaller, ActivityLog, DialogService,
            Path.Combine(TempDir, "Backups"), Path.Combine(TempDir, "Data"), Path.Combine(TempDir, "Logs"),
            Path.Combine(TempDir, "settings.json"), Path.Combine(TempDir, "Staged_UE4SS"));

        /// <summary>
        /// SettingsViewModel takes a real, concrete ImmMigrationService (not an interface) purely
        /// to hand off to Merge &amp; Install on a successful classic-IMM migration — none of these
        /// tests exercise that flow, so every one of ITS OWN dependencies here is a throwing stub;
        /// only ICredentialStore/INexusApiClient are the same shared-shape fakes used elsewhere.
        /// </summary>
        private static ImmMigrationService BuildImmMigrationService() => new(
            new UnusedLibraryRepository(), new UnusedDaedalusCatalogClient(), new UnusedJimk72CatalogClient(),
            new FakeNexusApiClient(), new FakeCredentialStore(), new UnusedPrebuiltPakImporter(),
            new FakeSettingsService(new AppSettings()), Path.GetTempPath());

        public void Dispose()
        {
            try
            {
                Directory.Delete(TempDir, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup — matches this codebase's own tolerance for a stray temp
                // folder over failing an otherwise-passing test run.
            }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class FakeSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;
        public int SaveCallCount { get; private set; }
        public bool SaveShouldSucceed { get; set; } = true;

        public bool Save()
        {
            SaveCallCount++;
            return SaveShouldSucceed;
        }
    }

    private sealed class FakeUnrealPakService : IUnrealPakService
    {
        public int ExtractCallCount { get; private set; }
        public Func<UnrealPakExtractResult>? ExtractResultFactory { get; set; }
        public Exception? ExtractException { get; set; }
        public string? HashToReturn { get; set; } = "fake-hash";

        public Task<UnrealPakExtractResult> ExtractDataPakAsync(
            string unrealPakExePath, string icarusContentPath, string outputDirectory,
            DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken = default)
        {
            ExtractCallCount++;
            if (ExtractException is not null)
            {
                throw ExtractException;
            }

            return Task.FromResult(ExtractResultFactory!());
        }

        public Task<string?> TryGetDataPakHashAsync(string icarusContentPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(HashToReturn);

        public Task<int> CreatePakAsync(string unrealPakExePath, string stagingDirectory, string outputPakPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<int> ExtractPakAsync(
            string unrealPakExePath, string pakFilePath, string outputDirectory,
            CancellationToken cancellationToken = default, string? filter = null) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<PakVerifyResult> VerifyPakAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");
    }

    private sealed class FakeWeeklyChangeReportStore : IWeeklyChangeReportStore
    {
        public int SaveCallCount { get; private set; }
        public WeeklyChangeReport? LastSavedReport { get; private set; }
        public WeeklyChangeReport? Current { get; private set; }
        public IReadOnlyList<WeeklyChangeReport> History => Current is null ? [] : [Current];

        public void Save(WeeklyChangeReport report)
        {
            SaveCallCount++;
            LastSavedReport = report;
            Current = report;
        }
    }

    private sealed class FakeUnrealPakInstaller : IUnrealPakInstaller
    {
        public string InstalledExePath { get; set; } = @"C:\fake\Tools\UnrealPak\Engine\Binaries\Win64\UnrealPak.exe";
        public bool PayloadAvailable { get; set; } = true;
        public int VerifyCallCount { get; private set; }
        public string? LastVerifiedExePath { get; private set; }
        public UnrealPakVerifyResult VerifyResult { get; set; } = new(UnrealPakHealth.Ok, "4.27.0", null);
        public int InstallCallCount { get; private set; }
        public string? InstallResultPath { get; set; }
        public Exception? InstallException { get; set; }

        public Task<UnrealPakVerifyResult> VerifyAsync(string exePath, CancellationToken cancellationToken = default)
        {
            VerifyCallCount++;
            LastVerifiedExePath = exePath;
            return Task.FromResult(VerifyResult);
        }

        public Task<string> InstallAsync(CancellationToken cancellationToken = default)
        {
            InstallCallCount++;
            if (InstallException is not null)
            {
                throw InstallException;
            }

            return Task.FromResult(InstallResultPath ?? InstalledExePath);
        }
    }

    private sealed class FakeSteamInstallLocator : ISteamInstallLocator
    {
        public string? FindIcarusContentPath() => null;

        public string? TryGetPersonaName(string steamId64) => null;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];

        public void Save(string target, string secret) => _values[target] = secret;

        public string? Read(string target) => _values.GetValueOrDefault(target);

        public void Delete(string target) => _values.Remove(target);
    }

    /// <summary>ValidateKeyAsync returns null (as if unauthenticated) by default — enough for InitializeNexusStatusAsync's own "no stored key" early return; every other member is unreachable from these tests.</summary>
    private sealed class FakeNexusApiClient : INexusApiClient
    {
        public Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<NexusUserInfo?>(null);

        public Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(
            string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<IReadOnlyList<NexusModInfo>> GetModListAsync(string apiKey, string gameDomain, NexusModList list, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<IReadOnlyList<NexusModInfo>> SearchModsAsync(string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<NexusModPage> ListAllModsAsync(string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<IReadOnlyList<NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");

        public Task<NexusEndorsementStatus> SetEndorsementAsync(string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");
    }

    private sealed class FakeNxmProtocolRegistrar : INxmProtocolRegistrar
    {
        public bool IsRegisteredToThisApp() => false;

        public void Register()
        {
        }

        public void Unregister()
        {
        }
    }

    private sealed class FakeUe4ssLoaderInstallService : IUe4ssLoaderInstallService
    {
        public Ue4ssLoaderStatus StatusToReturn { get; set; } = new(false, null);
        public int InstallOrUpdateCallCount { get; private set; }
        public string? LastIcarusContentPath { get; private set; }
        public string? LastDownloadedZipPath { get; private set; }
        public string? LastBackupDirectory { get; private set; }
        public Exception? InstallOrUpdateException { get; set; }

        /// <summary>Applied to StatusToReturn after a successful InstallOrUpdateAsync — mirrors the real service actually changing what's on disk, so a test can assert RefreshUe4ssStatus picked up the change.</summary>
        public Ue4ssLoaderStatus? StatusAfterInstall { get; set; }
        public Ue4ssLoaderStatus? StatusAfterUninstall { get; set; }
        public IReadOnlyList<string> UserAddedMods { get; set; } = [];
        public int UninstallCallCount { get; private set; }
        public Ue4ssUninstallResult UninstallResult { get; set; } = new([], "backup.zip");
        public Exception? UninstallException { get; set; }

        public Ue4ssLoaderStatus GetStatus(string icarusContentPath) => StatusToReturn;

        public Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, CancellationToken cancellationToken = default)
        {
            InstallOrUpdateCallCount++;
            LastIcarusContentPath = icarusContentPath;
            LastDownloadedZipPath = downloadedZipPath;
            LastBackupDirectory = backupDirectory;
            if (InstallOrUpdateException is not null)
            {
                throw InstallOrUpdateException;
            }

            if (StatusAfterInstall is { } status)
            {
                StatusToReturn = status;
            }

            return Task.CompletedTask;
        }

        public IReadOnlyList<string> ListUserAddedMods(string icarusContentPath) => UserAddedMods;

        public bool IsFrameworkOwned(string icarusContentPath, string modName) => false;

        public Task<Ue4ssUninstallResult> UninstallAsync(string icarusContentPath, string stagedModsDirectory, string backupDirectory, CancellationToken cancellationToken = default)
        {
            UninstallCallCount++;
            if (UninstallException is not null)
            {
                throw UninstallException;
            }

            if (StatusAfterUninstall is { } status)
            {
                StatusToReturn = status;
            }

            return Task.FromResult(UninstallResult);
        }
    }

    private sealed class FakeUe4ssReleaseClient : IUe4ssReleaseClient
    {
        public Ue4ssReleaseInfo? ReleaseToReturn { get; set; }

        public Task<Ue4ssReleaseInfo?> GetLatestStableReleaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ReleaseToReturn);
    }

    private sealed class FakeAppUpdateClient : IAppUpdateClient
    {
        public AppUpdateRelease? ReleaseToReturn { get; set; }

        public Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ReleaseToReturn);

        public Task DownloadAssetAsync(AppUpdateRelease release, string destinationPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");
    }

    private sealed class FakeThemeService : IThemeService
    {
        public IReadOnlyList<string> AvailableThemes => [];

        public void ApplyTheme(string themeName)
        {
        }

        public IReadOnlyDictionary<string, string> GetThemeColors(string themeName) => new Dictionary<string, string>();
    }

    private sealed class FakeCustomSkinStore : ICustomSkinStore
    {
        public string FilePath => "fake-skin.json";

        public CustomSkin? Load() => null;

        public void Save(CustomSkin skin)
        {
        }
    }

    private sealed class FakeActivityLog : IActivityLog
    {
        public ObservableCollection<ActivityEntry> Entries { get; } = [];

        public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info) =>
            Entries.Insert(0, new ActivityEntry(message, kind, DateTimeOffset.Now));
    }

    /// <summary>Same shape as SavesViewModelTests' own FakeDialogService — copied deliberately rather than reinvented, per this session's own established convention.</summary>
    private sealed class FakeDialogService(bool confirmResult) : IDialogService
    {
        public int ConfirmCallCount { get; private set; }
        public string? LastConfirmMessage { get; private set; }
        public string? LastConfirmTitle { get; private set; }

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            ConfirmCallCount++;
            LastConfirmMessage = message;
            LastConfirmTitle = title;
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

    // --- Throwing stubs that exist only so a real ImmMigrationService can be constructed for
    //     SettingsViewModel's own constructor — none of these are exercised by any test here. ---

    private sealed class UnusedLibraryRepository : ILibraryRepository
    {
        public IReadOnlyList<LibraryEntry> GetAll() => throw new NotSupportedException("not exercised by these tests");
        public IReadOnlyList<string> UnreadableFolders => throw new NotSupportedException("not exercised by these tests");
        public IReadOnlyList<LibraryEntry> Search(string query) => throw new NotSupportedException("not exercised by these tests");
        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null) => throw new NotSupportedException("not exercised by these tests");
        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null) => throw new NotSupportedException("not exercised by these tests");
        public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version) => throw new NotSupportedException("not exercised by these tests");
        public void Refresh() => throw new NotSupportedException("not exercised by these tests");
        public void Delete(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes) => throw new NotSupportedException("not exercised by these tests");
        public void MarkLocallyEdited(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public void MarkConvertedFromPrebuiltPak(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public void SetDisplayNameOverride(string folderName, string? displayName) => throw new NotSupportedException("not exercised by these tests");
        public void LinkToNexus(string folderName, int nexusModId) => throw new NotSupportedException("not exercised by these tests");
        public void SetCatalogEntry(string folderName, string catalogEntryId) => throw new NotSupportedException("not exercised by these tests");
        public string BackupMod(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public bool HasModBackup(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public bool RestoreLatestModBackup(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public string? TryGetLatestModBackupPath(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public LibraryEntry CreateBlankMod(string name, string author, ModTemplate template = ModTemplate.Blank) => throw new NotSupportedException("not exercised by these tests");
        public IReadOnlyList<string> ListAssetPaths(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public IReadOnlyList<string> ListAssetPaths(string folderName, IReadOnlyList<string> precomputedFiles) => throw new NotSupportedException("not exercised by these tests");
        public byte[] ReadAssetContent(string folderName, string relativePath) => throw new NotSupportedException("not exercised by these tests");
        public string? ReadReadme(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public string? ReadReadme(string folderName, IReadOnlyList<string> precomputedFiles) => throw new NotSupportedException("not exercised by these tests");
        public IReadOnlyList<string> ListFolderFiles(string folderName) => throw new NotSupportedException("not exercised by these tests");
        public string GetFolderPath(string folderName) => throw new NotSupportedException("not exercised by these tests");
    }

    private sealed class UnusedDaedalusCatalogClient : IDaedalusCatalogClient
    {
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");
    }

    private sealed class UnusedJimk72CatalogClient : IJimk72CatalogClient
    {
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");
    }

    private sealed class UnusedPrebuiltPakImporter : IPrebuiltPakImporter
    {
        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by these tests");
    }
}
