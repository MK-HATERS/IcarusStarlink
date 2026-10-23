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
/// thing): the constructor itself starts several async checks (CheckForGameUpdateAsync,
/// InitializeNexusStatusAsync, RunFirstLaunchSetupAsync — which runs EnsureUnrealPakOnLaunchAsync —
/// then CheckForAppUpdatesAfterSetupAsync and RefreshBackupsSizeAsync). Only first-launch setup has
/// a seam to await (FirstLaunchSetupTask); the rest are fire-and-forget. Harness's own
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
[Collection(MessengerCollection.Name)]
public sealed class SettingsViewModelTests
{
    private static WeeklyChangeReport MakeReport(int changedFileCount) => new(
        DateTimeOffset.UtcNow.AddDays(-7),
        DateTimeOffset.UtcNow,
        Enumerable.Range(0, changedFileCount)
            .Select(i => new ChangedDataFile($"Crafting/D_Fuel{i}.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: [], FieldChanges: []))
            .ToList());

    private static HttpMessageHandler CountingHandler(Action onRequest) =>
        new StubHttpMessageHandler(_ =>
        {
            onRequest();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });

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

    /// <summary>
    /// Regression: a second request while an extraction ran started another one into the same
    /// Data folder, and RefreshGameDataAsync answered "done" at once while the first still ran.
    /// </summary>
    [Fact]
    public async Task RefreshGameData_WhileOneRuns_JoinsItInsteadOfStartingAnother()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        harness.UnrealPakService.ExtractResultFactory = () => new UnrealPakExtractResult(10, null);
        var gate = new TaskCompletionSource();
        harness.UnrealPakService.ExtractGate = gate;

        var first = vm.RefreshGameDataAsync();
        var second = vm.UpdateDataFolderCommand.ExecuteAsync(null);
        var third = vm.RefreshGameDataAsync();

        Assert.False(third.IsCompleted, "a caller was told the data was refreshed while the extraction still ran");
        Assert.False(vm.UpdateDataFolderCommand.CanExecute(null));
        gate.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, harness.UnrealPakService.ExtractCallCount);
        Assert.True(vm.UpdateDataFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpdateDataFolderCommand_ExtractThrows_UnrealPakHealthy_ShowsRawExceptionMessage()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        harness.UnrealPakService.ExtractException = new InvalidOperationException("disk full");
        harness.UnrealPakInstaller.VerifyResult = new UnrealPakVerifyResult(UnrealPakHealth.Ok, "4.27.0", null);

        await vm.UpdateDataFolderCommand.ExecuteAsync(null);

        Assert.Equal("Refresh failed: disk full", vm.DataFolderStatusMessage);
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

    /// <summary>No check pressed: Install uses the UE4SS dev build built into Starlink, hash-checked, in the dev-build layout with its licence.</summary>
    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_NoGitHub_InstallsTheBundledCopy()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        harness.Ue4ssLoaderInstallService.StatusAfterInstall = new Ue4ssLoaderStatus(true, BundledUe4ss.Version, BundledUe4ss.Commit);
        var vm = harness.BuildViewModel();
        Assert.True(vm.CanInstallOrUpdateUe4ss);
        Assert.Equal($"Install {BundledUe4ss.Label}", vm.Ue4ssInstallButtonLabel);
        Assert.Equal("3.0.1 dev build 1181", BundledUe4ss.Label);

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.Contains("UE4SS_bundled_", harness.Ue4ssLoaderInstallService.LastDownloadedZipPath);
        Assert.Contains("ue4ss/UE4SS.dll", harness.Ue4ssLoaderInstallService.LastZipEntries);
        Assert.Contains("dwmapi.dll", harness.Ue4ssLoaderInstallService.LastZipEntries);
        Assert.Contains("ue4ss/LICENSE", harness.Ue4ssLoaderInstallService.LastZipEntries);
        Assert.Equal(BundledUe4ss.Commit, harness.Ue4ssLoaderInstallService.LastKnownCommit);
        Assert.False(File.Exists(harness.Ue4ssLoaderInstallService.LastDownloadedZipPath));
        Assert.Equal($"Installed UE4SS {BundledUe4ss.Label}.", vm.Ue4ssStatusMessage);
        Assert.Equal("Reinstall", vm.Ue4ssInstallButtonLabel);
    }

    /// <summary>Regression: the Library's "install UE4SS" bar runs this command directly, and nothing stopped it starting while an uninstall (or another install) was writing Binaries\Win64.</summary>
    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_WhileUninstalling_DoesNothing()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        var vm = harness.BuildViewModel();
        vm.IsUninstallingUe4ss = true;

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
    }

    /// <summary>GitHub is only asked when the user presses Check for updates, never on its own.</summary>
    [Fact]
    public void Constructor_DoesNotAskGitHubAboutUe4ss()
    {
        using var harness = new Harness();
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1", "1c1a1497");

        var vm = harness.BuildViewModel();

        Assert.Equal(0, harness.Ue4ssReleaseClient.DevBuildCallCount);
        Assert.Equal("3.0.1 dev build 1c1a1497 installed.", vm.Ue4ssStatusText);
        Assert.Equal($"Install {BundledUe4ss.Label}", vm.Ue4ssInstallButtonLabel);
    }

    /// <summary>The check found the same build that comes with Starlink: no download.</summary>
    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_GitHubSameAsBundled_UsesTheBundledCopy()
    {
        using var harness = new Harness(dialogConfirmResult: true, httpHandler: SucceedingHandler([1, 2, 3]));
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo(BundledUe4ss.Version, "https://example.invalid/UE4SS.zip", null, BundledUe4ss.BuildNumber, BundledUe4ss.Commit);
        var vm = harness.BuildViewModel();

        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);
        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Contains("UE4SS_bundled_", harness.Ue4ssLoaderInstallService.LastDownloadedZipPath);
    }

    /// <summary>A manual check finds a newer dev build: Update offers it, and the confirmation names the user's C++ mods, which are tied to one build.</summary>
    [Fact]
    public async Task CheckForUe4ssUpdate_NewerBuild_OffersUpdateAndWarnsAboutCppMods()
    {
        using var harness = new Harness(dialogConfirmResult: false);
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1", "1c1a1497");
        harness.Ue4ssLoaderInstallService.NativeMods = ["IcarusStutterFix", "NoStability"];
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", null, 1200, "abcdef12");
        harness.Ue4ssReleaseClient.OrderToReturn = Ue4ssBuildOrder.Newer;
        var vm = harness.BuildViewModel();

        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Ue4ssReleaseClient.DevBuildCallCount);
        Assert.Equal(("1c1a1497", "abcdef12"), harness.Ue4ssReleaseClient.LastCompared);
        Assert.Equal("Update to 3.0.1 dev build 1200", vm.Ue4ssInstallButtonLabel);
        Assert.Equal("3.0.1 dev build 1c1a1497 installed — 3.0.1 dev build 1200 available.", vm.Ue4ssStatusText);
        Assert.Equal("UE4SS 3.0.1 dev build 1200 is available.", vm.Ue4ssStatusMessage);

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal("Update UE4SS", harness.DialogService.LastConfirmTitle);
        Assert.Contains("downloads UE4SS 3.0.1 dev build 1200 from GitHub", harness.DialogService.LastConfirmMessage);
        Assert.Contains("IcarusStutterFix, NoStability", harness.DialogService.LastConfirmMessage);
        Assert.Contains("C++ mods", harness.DialogService.LastConfirmMessage);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
    }

    /// <summary>Already on the newest build: up to date, Reinstall, and no C++ warning (nothing changes).</summary>
    [Fact]
    public async Task CheckForUe4ssUpdate_SameBuild_SaysUpToDate()
    {
        using var harness = new Harness(dialogConfirmResult: false);
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1", "abcdef12");
        harness.Ue4ssLoaderInstallService.NativeMods = ["NoStability"];
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", null, 1200, "abcdef12");
        var vm = harness.BuildViewModel();

        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);
        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal("You have the newest UE4SS dev build.", vm.Ue4ssStatusMessage);
        Assert.Equal("3.0.1 dev build 1200 installed (up to date).", vm.Ue4ssStatusText);
        Assert.Equal("Reinstall", vm.Ue4ssInstallButtonLabel);
        Assert.DoesNotContain("C++", harness.DialogService.LastConfirmMessage);
    }

    /// <summary>The old official v3.0.1 is older than any dev build, without asking GitHub to compare.</summary>
    [Fact]
    public void OfficialRelease_IsOfferedTheBundledDevBuildAsAnUpdate()
    {
        using var harness = new Harness();
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1", Ue4ssBuild.OfficialReleaseCommit[..8]);

        var vm = harness.BuildViewModel();

        Assert.Equal($"Update to {BundledUe4ss.Label}", vm.Ue4ssInstallButtonLabel);
        Assert.StartsWith("3.0.1 (official release) installed", vm.Ue4ssStatusText);
    }

    [Fact]
    public async Task CheckForUe4ssUpdate_GitHubDown_SaysSoAndKeepsTheBundledBuild()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();

        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);

        Assert.Contains("Couldn't check for a newer UE4SS", vm.Ue4ssStatusMessage);
        Assert.Equal($"Install {BundledUe4ss.Label}", vm.Ue4ssInstallButtonLabel);
    }

    [Fact]
    public async Task InstallUe4ssFromZip_InstallsThePickedZip_AndCancelDoesNothing()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        harness.Ue4ssLoaderInstallService.StatusAfterInstall = new Ue4ssLoaderStatus(true, "3.0.1", "1ba38db4");
        var vm = harness.BuildViewModel();
        vm.PickUe4ssZip = () => null;

        await vm.InstallUe4ssFromZipCommand.ExecuteAsync(null);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);

        vm.PickUe4ssZip = () => @"C:\Downloads\UE4SS_v3.0.1-1176-g1ba38db4.zip";
        await vm.InstallUe4ssFromZipCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Downloads\UE4SS_v3.0.1-1176-g1ba38db4.zip", harness.Ue4ssLoaderInstallService.LastDownloadedZipPath);
        // The zip's GitHub name says which build it is, so Starlink records it.
        Assert.Equal("1ba38db4", harness.Ue4ssLoaderInstallService.LastKnownCommit);
        Assert.Equal("Installed UE4SS 3.0.1 dev build 1ba38db4 from UE4SS_v3.0.1-1176-g1ba38db4.zip.", vm.Ue4ssStatusMessage);
        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
    }

    [Fact]
    public async Task InstallUe4ssFromZip_ABadZip_ReportsTheInstallersReason()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        harness.Ue4ssLoaderInstallService.InstallOrUpdateException = new InvalidOperationException("This UE4SS archive doesn't have the expected layout");
        var vm = harness.BuildViewModel();
        vm.PickUe4ssZip = () => @"C:\Downloads\something.zip";

        await vm.InstallUe4ssFromZipCommand.ExecuteAsync(null);

        Assert.Contains("expected layout", vm.Ue4ssStatusMessage);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Theory]
    [InlineData("3.1.0", "3.0.1", true)]
    [InlineData("v3.0.2", "3.0.1", true)]
    [InlineData("3.0.1", "3.0.1", false)]
    [InlineData("3.0.0", "3.0.1", false)]
    [InlineData("3.0.1", null, true)]
    [InlineData("nonsense", "3.0.1", false)]
    public void BundledUe4ss_IsNewer_ComparesVersions(string candidate, string? current, bool expected) =>
        Assert.Equal(expected, BundledUe4ss.IsNewer(candidate, current));

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_Declined_DoesNotDownloadOrInstall()
    {
        // The check runs first so the install would really download (the GitHub build is newer
        // than the bundled one); without it the bundled copy is used and no download is possible.
        var requests = 0;
        using var harness = new Harness(dialogConfirmResult: false, httpHandler: CountingHandler(() => requests++));
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", null, 1200, "abcdef12");
        var vm = harness.BuildViewModel();
        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(0, requests);
        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_Confirmed_DownloadsInstallsAndRefreshesStatus()
    {
        var payloadBytes = new byte[] { 1, 2, 3, 4 };
        using var harness = new Harness(dialogConfirmResult: true, httpHandler: SucceedingHandler(payloadBytes));
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", null, 1200, "abcdef12");
        harness.Ue4ssLoaderInstallService.StatusAfterInstall = new Ue4ssLoaderStatus(true, "3.0.1", "abcdef12");
        var vm = harness.BuildViewModel();

        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);
        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.Equal(vm.IcarusContentPath, harness.Ue4ssLoaderInstallService.LastIcarusContentPath);
        Assert.Contains("UE4SS_", harness.Ue4ssLoaderInstallService.LastDownloadedZipPath);
        Assert.False(File.Exists(harness.Ue4ssLoaderInstallService.LastDownloadedZipPath));
        Assert.True(vm.IsUe4ssInstalled);
        Assert.Equal("abcdef12", vm.Ue4ssInstalledCommit);
        Assert.Equal("abcdef12", harness.Ue4ssLoaderInstallService.LastKnownCommit);
        Assert.Equal("Installed UE4SS 3.0.1 dev build 1200.", vm.Ue4ssStatusMessage);
        Assert.Empty(harness.Ue4ssLoaderInstallService.LastZipEntries); // the fake download isn't a real zip
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_DownloadFails_ShowsInstallFailedMessage()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", null, 1200, "abcdef12");
        var vm = harness.BuildViewModel();

        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);
        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Contains("Install failed:", vm.Ue4ssStatusMessage);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_DigestMismatch_AbortsBeforeInstallAndShowsFailedMessage()
    {
        // A real bug found live: unlike this app's own self-update (AppUpdateClient), the UE4SS
        // loader download had no integrity check at all — a corrupted or tampered download would
        // be handed straight to InstallOrUpdateAsync and written into the game's own
        // Binaries\Win64 folder with nothing to catch it.
        var payloadBytes = "zip-bytes"u8.ToArray();
        using var harness = new Harness(dialogConfirmResult: true, httpHandler: SucceedingHandler(payloadBytes));
        var wrongDigest = "sha256:" + new string('0', 64);
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", wrongDigest, 1200, "abcdef12");
        var vm = harness.BuildViewModel();

        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);
        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.Contains("Install failed:", vm.Ue4ssStatusMessage);
        Assert.Contains("integrity verification", vm.Ue4ssStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_ServiceThrows_WhenAlreadyInstalled_ShowsUpdateFailedMessage()
    {
        var payloadBytes = new byte[] { 9, 9 };
        using var harness = new Harness(dialogConfirmResult: true, httpHandler: SucceedingHandler(payloadBytes));
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", null, 1200, "abcdef12");
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1", "1c1a1497");
        harness.Ue4ssReleaseClient.OrderToReturn = Ue4ssBuildOrder.Newer;
        harness.Ue4ssLoaderInstallService.InstallOrUpdateException = new IOException("locked file");
        var vm = harness.BuildViewModel();
        Assert.True(vm.IsUe4ssInstalled);

        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);
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

    /// <summary>
    /// Regression guard: Install/uninstall write into the game's own Binaries\Win64 folder
    /// (dwmapi.dll, UE4SS.dll) — files the running game process can hold open, exactly the class of
    /// risk SavesViewModel already guards against via IGameProcessChecker. This used to never check
    /// at all.
    /// </summary>
    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_GameRunning_RefusesAndNeverDownloads()
    {
        var requests = 0;
        using var harness = new Harness(dialogConfirmResult: true, httpHandler: CountingHandler(() => requests++), gameProcessChecker: new FakeGameProcessChecker(true));
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", null, 1200, "abcdef12");
        var vm = harness.BuildViewModel();
        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null);

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(0, requests);
        Assert.Contains("Icarus is running", vm.Ue4ssStatusMessage);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.False(vm.IsInstallingUe4ss);
    }

    /// <summary>Review 2026-10-23: the Library's "Install UE4SS" bar runs this command with Settings not showing, so a refusal only on Settings' status line was no answer at all.</summary>
    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_Refused_AlsoLogsAWarning()
    {
        using var harness = new Harness(dialogConfirmResult: true, gameProcessChecker: new FakeGameProcessChecker(true));
        var vm = harness.BuildViewModel();

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Contains(harness.ActivityLog.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message.Contains("Icarus is running"));
    }

    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_Fails_AlsoLogsAWarning()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        harness.Ue4ssLoaderInstallService.InstallOrUpdateException = new IOException("locked file");
        var vm = harness.BuildViewModel();

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Contains(harness.ActivityLog.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message == "UE4SS install failed: locked file");
    }

    /// <summary>Review 2026-10-23: Browse and Auto-detect only fill the box until Save settings, but UE4SS installed into the box's folder.</summary>
    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_UsesTheSavedGameFolder_NotAnUnsavedOne()
    {
        using var harness = new Harness(dialogConfirmResult: true);
        var saved = harness.Settings.IcarusContentPath;
        var vm = harness.BuildViewModel();
        vm.IcarusContentPath = Path.Combine(harness.TempDir, "SomeOtherGame", "Content");

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Equal(saved, harness.Ue4ssLoaderInstallService.LastIcarusContentPath);
    }

    /// <summary>The late, immediately-before-the-write recheck: not running at the first check (before the confirm dialog), but running by the time the download finished.</summary>
    [Fact]
    public async Task InstallOrUpdateUe4ssCommand_GameStartsRunningDuringDownload_LateCheckRefusesBeforeInstalling()
    {
        var payloadBytes = new byte[] { 1, 2, 3 };
        using var harness = new Harness(
            dialogConfirmResult: true, httpHandler: SucceedingHandler(payloadBytes), gameProcessChecker: new FakeGameProcessChecker(false, true));
        harness.Ue4ssReleaseClient.DevBuildToReturn = new Ue4ssReleaseInfo("3.0.1", "https://example.invalid/UE4SS.zip", null, 1200, "abcdef12");
        var vm = harness.BuildViewModel();
        await vm.CheckForUe4ssUpdateCommand.ExecuteAsync(null); // so the install downloads, as the name says

        await vm.InstallOrUpdateUe4ssCommand.ExecuteAsync(null);

        Assert.Contains("started running", vm.Ue4ssStatusMessage);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.InstallOrUpdateCallCount);
        Assert.False(vm.IsInstallingUe4ss);
    }

    [Fact]
    public async Task UninstallUe4ssCommand_GameRunning_RefusesAndNeverUninstalls()
    {
        using var harness = new Harness(dialogConfirmResult: true, gameProcessChecker: new FakeGameProcessChecker(true));
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1");
        var vm = harness.BuildViewModel();

        await vm.UninstallUe4ssCommand.ExecuteAsync(null);

        Assert.Contains("Icarus is running", vm.Ue4ssStatusMessage);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.UninstallCallCount);
        Assert.True(vm.IsUe4ssInstalled);
    }

    /// <summary>The late, immediately-before-the-write recheck: not running at the first check (before the confirm dialog), but running by the time the user answered it.</summary>
    [Fact]
    public async Task UninstallUe4ssCommand_GameStartsRunningAfterConfirm_LateCheckRefusesBeforeUninstalling()
    {
        using var harness = new Harness(dialogConfirmResult: true, gameProcessChecker: new FakeGameProcessChecker(false, true));
        harness.Ue4ssLoaderInstallService.StatusToReturn = new Ue4ssLoaderStatus(true, "3.0.1");
        var vm = harness.BuildViewModel();

        await vm.UninstallUe4ssCommand.ExecuteAsync(null);

        Assert.Contains("started running", vm.Ue4ssStatusMessage);
        Assert.Equal(0, harness.Ue4ssLoaderInstallService.UninstallCallCount);
        Assert.True(vm.IsUe4ssInstalled);
    }

    // =====================================================================================
    // Harness — builds a real SettingsViewModel with a fake per dependency (this codebase's own
    // established convention — see SavesViewModelTests). Defaults are chosen so the constructor's
    // own five fire-and-forget launch checks all resolve harmlessly (see this file's own top doc
    // comment) without needing to touch any of them from a given test.
    // =====================================================================================

    // =====================================================================================
    // First-launch setup — RunFirstLaunchSetupAsync (game folder → UnrealPak → first extraction)
    // =====================================================================================

    [Fact]
    public async Task FirstLaunch_NoGameFolder_FoundViaSteam_SavesItSilentlyAndFlagsAttention()
    {
        using var harness = new Harness();
        harness.Settings.IcarusContentPath = null;
        var detected = Path.Combine(harness.TempDir, "Steam", "steamapps", "common", "Icarus", "Icarus", "Content");
        harness.SteamInstallLocator.ContentPathToReturn = detected;

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;

        Assert.Equal(detected, vm.IcarusContentPath);
        Assert.Equal(detected, harness.Settings.IcarusContentPath);
        Assert.Contains("Steam", vm.SavedMessage);
        Assert.Equal(0, harness.DialogService.ConfirmCallCount);
        Assert.True(vm.FirstLaunchSetupNeededAttention);
    }

    [Fact]
    public async Task FirstLaunch_NoGameFolder_SteamCantFindIt_OffersBrowse_DeclinedLeavesGuidance()
    {
        using var harness = new Harness(dialogConfirmResult: false);
        harness.Settings.IcarusContentPath = null;

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;

        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Contains("Icarus folder", harness.DialogService.LastConfirmTitle);
        Assert.Null(vm.IcarusContentPath);
        Assert.Contains("Auto-detect or Browse", vm.SavedMessage);
        Assert.True(vm.FirstLaunchSetupNeededAttention);
    }

    [Fact]
    public async Task FirstLaunch_EverythingAlreadySet_IsSilent()
    {
        using var harness = new Harness();
        harness.SteamInstallLocator.ContentPathToReturn = @"C:\somewhere\else\Content";
        var configured = harness.Settings.IcarusContentPath;

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;

        Assert.Equal(configured, vm.IcarusContentPath);
        Assert.Equal(0, harness.DialogService.ConfirmCallCount);
        Assert.Equal(0, harness.UnrealPakService.ExtractCallCount);
        Assert.False(vm.FirstLaunchSetupNeededAttention);
    }

    [Fact]
    public async Task FirstLaunch_PrerequisitesSet_DataNeverExtracted_OffersExtraction_AcceptedRunsItOnce()
    {
        using var harness = new Harness();
        harness.Settings.FirstLaunchSetupCompletedAtUtc = null;
        harness.Settings.LastDataFolderUpdatedAt = null;
        harness.UnrealPakService.ExtractResultFactory = () => new UnrealPakExtractResult(10, null);

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;

        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Contains("Extract game data", harness.DialogService.LastConfirmTitle);
        Assert.Equal(1, harness.UnrealPakService.ExtractCallCount);
        Assert.NotNull(harness.Settings.LastDataFolderUpdatedAt);
        Assert.NotNull(harness.Settings.FirstLaunchSetupCompletedAtUtc);
        Assert.True(vm.FirstLaunchSetupNeededAttention);
    }

    [Fact]
    public async Task FirstLaunch_ExtractionDeclined_NeverExtracts_StillMarksSetupCompleted()
    {
        using var harness = new Harness(dialogConfirmResult: false);
        harness.Settings.FirstLaunchSetupCompletedAtUtc = null;
        harness.Settings.LastDataFolderUpdatedAt = null;

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;

        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Equal(0, harness.UnrealPakService.ExtractCallCount);
        Assert.Contains("Refresh game data", vm.DataFolderStatusMessage);
        Assert.NotNull(harness.Settings.FirstLaunchSetupCompletedAtUtc);
    }

    /// <summary>An install that predates the flow and already extracted its data gets no offer — just marked done so the check stops running.</summary>
    /// <summary>Settings › Maintenance › "Check for updates when Starlink starts" (user request 2026-10-23): off means no look at launch; the button still checks.</summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task LaunchUpdateCheck_FollowsTheAutoCheckSetting(bool autoCheck, int expectedCalls)
    {
        using var harness = new Harness();
        harness.Settings.AutoCheckAppUpdates = autoCheck;

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;
        for (var i = 0; i < 50 && harness.AppUpdateClient.LatestReleaseCalls < expectedCalls; i++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);
        Assert.Equal(expectedCalls, harness.AppUpdateClient.LatestReleaseCalls);
    }

    [Fact]
    public async Task FirstLaunch_NxmHandlerNotRegistered_OffersToRegister_AcceptedRegisters()
    {
        using var harness = new Harness();
        harness.Settings.FirstLaunchSetupCompletedAtUtc = null;
        harness.Settings.LastDataFolderUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        harness.NxmProtocolRegistrar.Registered = false;

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;

        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Contains("Nexus download links", harness.DialogService.LastConfirmTitle);
        Assert.Equal(1, harness.NxmProtocolRegistrar.RegisterCallCount);
        Assert.NotNull(harness.Settings.FirstLaunchSetupCompletedAtUtc);
    }

    [Fact]
    public async Task FirstLaunch_DataAlreadyExtractedBefore_NeverOffers_MarksCompleted()
    {
        using var harness = new Harness();
        harness.Settings.FirstLaunchSetupCompletedAtUtc = null;
        harness.Settings.LastDataFolderUpdatedAt = DateTimeOffset.UtcNow.AddDays(-3);

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;

        Assert.Equal(0, harness.DialogService.ConfirmCallCount);
        Assert.Equal(0, harness.UnrealPakService.ExtractCallCount);
        Assert.NotNull(harness.Settings.FirstLaunchSetupCompletedAtUtc);
        Assert.False(vm.FirstLaunchSetupNeededAttention);
    }

    /// <summary>Game folder still missing after step 1 (Steam can't find it, Browse declined) — the extraction offer must wait for a later launch, not fire against nothing, and must NOT be marked completed.</summary>
    [Fact]
    public async Task FirstLaunch_GameFolderStillMissing_ExtractionOfferWaitsForALaterLaunch()
    {
        using var harness = new Harness(dialogConfirmResult: false);
        harness.Settings.IcarusContentPath = null;
        harness.Settings.FirstLaunchSetupCompletedAtUtc = null;

        var vm = harness.BuildViewModel();
        await vm.FirstLaunchSetupTask;

        Assert.Equal(1, harness.DialogService.ConfirmCallCount);
        Assert.Equal(0, harness.UnrealPakService.ExtractCallCount);
        Assert.Null(harness.Settings.FirstLaunchSetupCompletedAtUtc);
    }

    private sealed class Harness : IDisposable
    {
        public readonly string TempDir;
        public readonly string ValidUnrealPakExePath;
        public readonly AppSettings Settings = new();
        public readonly FakeSettingsService SettingsService;
        public readonly FakeUnrealPakService UnrealPakService = new();
        public readonly FakeWeeklyChangeReportStore WeeklyChangeReportStore = new();
        public readonly FakeUnrealPakInstaller UnrealPakInstaller = new();
        public readonly FakeSteamInstallLocator SteamInstallLocator = new();
        // Registered by default so the one-time first-launch nxm:// offer never fires from the
        // constructor here; the dedicated test below sets Registered = false.
        public readonly FakeNxmProtocolRegistrar NxmProtocolRegistrar = new() { Registered = true };
        public readonly FakeUe4ssLoaderInstallService Ue4ssLoaderInstallService = new();
        public readonly FakeUe4ssReleaseClient Ue4ssReleaseClient = new();
        public readonly FakeAppUpdateClient AppUpdateClient = new();
        public readonly FakeDialogService DialogService;
        public readonly FakeActivityLog ActivityLog = new();
        public readonly FakeGameProcessChecker GameProcessChecker;
        private readonly HttpMessageHandler _httpHandler;

        public Harness(bool dialogConfirmResult = true, HttpMessageHandler? httpHandler = null, FakeGameProcessChecker? gameProcessChecker = null)
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
            // Same reasoning: already "completed", so the one-time first-data-extraction offer
            // (RunFirstLaunchSetupAsync's third step) never fires from the constructor here. The
            // first-launch tests below set this back to null deliberately.
            Settings.FirstLaunchSetupCompletedAtUtc = DateTimeOffset.UtcNow;
            SettingsService = new FakeSettingsService(Settings);
            DialogService = new FakeDialogService(dialogConfirmResult);
            GameProcessChecker = gameProcessChecker ?? new FakeGameProcessChecker(false);
            _httpHandler = httpHandler ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public SettingsViewModel BuildViewModel() => new(
            SettingsService, UnrealPakService, WeeklyChangeReportStore,
            SteamInstallLocator, new FakeCredentialStore(), new FakeNexusApiClient(),
            NxmProtocolRegistrar, Ue4ssLoaderInstallService, Ue4ssReleaseClient, AppUpdateClient,
            new HttpClient(_httpHandler), new FakeThemeService(), new FakeCustomSkinStore(),
            BuildImmMigrationService(),
            () => throw new InvalidOperationException("MergeInstallViewModel factory not exercised by these tests"),
            UnrealPakInstaller, ActivityLog, DialogService, GameProcessChecker,
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

        /// <summary>When set, an extraction waits for it (a slow one), so a second request can arrive meanwhile.</summary>
        public TaskCompletionSource? ExtractGate { get; set; }

        public async Task<UnrealPakExtractResult> ExtractDataPakAsync(
            string unrealPakExePath, string icarusContentPath, string outputDirectory,
            DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken = default)
        {
            ExtractCallCount++;
            if (ExtractGate is { } gate)
            {
                await gate.Task;
            }

            if (ExtractException is not null)
            {
                throw ExtractException;
            }

            return ExtractResultFactory!();
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
        public string? ContentPathToReturn { get; set; }

        public string? FindIcarusContentPath() => ContentPathToReturn;

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
        public bool Registered { get; set; }
        public int RegisterCallCount { get; private set; }

        public bool IsRegisteredToThisApp() => Registered;

        public string? CurrentHandlerCommand() => Registered ? "\"C:\\IcarusStarlink\\IcarusStarlink.App.exe\" \"%1\"" : null;

        public string? PreviousHandlerCommand() => null;

        public void Register()
        {
            RegisterCallCount++;
            Registered = true;
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
        public List<string> LastZipEntries { get; private set; } = [];
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
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(downloadedZipPath);
                LastZipEntries = [.. zip.Entries.Select(e => e.FullName)];
            }
            catch (Exception)
            {
                LastZipEntries = []; // not a real zip (the fake download) or a made-up path
            }

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

        public string? LastKnownCommit { get; private set; }
        public IReadOnlyList<string> NativeMods { get; set; } = [];

        public Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, string? knownVersion, string? knownCommit, CancellationToken cancellationToken = default)
        {
            LastKnownCommit = knownCommit;
            return InstallOrUpdateAsync(icarusContentPath, downloadedZipPath, backupDirectory, cancellationToken);
        }

        public IReadOnlyList<string> ListNativeMods(string icarusContentPath) => NativeMods;

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
        public Task<Ue4ssReleaseInfo?> GetLatestStableReleaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Ue4ssReleaseInfo?>(null);

        public Ue4ssReleaseInfo? DevBuildToReturn { get; set; }
        public int DevBuildCallCount { get; private set; }
        public Ue4ssBuildOrder OrderToReturn { get; set; } = Ue4ssBuildOrder.Unknown;
        public (string Installed, string Candidate)? LastCompared { get; private set; }

        public Task<Ue4ssReleaseInfo?> GetLatestDevBuildAsync(CancellationToken cancellationToken = default)
        {
            DevBuildCallCount++;
            return Task.FromResult(DevBuildToReturn);
        }

        public Task<Ue4ssBuildOrder> CompareBuildsAsync(string installedCommit, string candidateCommit, CancellationToken cancellationToken = default)
        {
            LastCompared = (installedCommit, candidateCommit);
            return Task.FromResult(Ue4ssBuild.SameCommit(installedCommit, candidateCommit) ? Ue4ssBuildOrder.Same : OrderToReturn);
        }
    }

    private sealed class FakeAppUpdateClient : IAppUpdateClient
    {
        public AppUpdateRelease? ReleaseToReturn { get; set; }
        public int LatestReleaseCalls { get; private set; }

        public Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
        {
            LatestReleaseCalls++;
            return Task.FromResult(ReleaseToReturn);
        }

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

    /// <summary>Same shape as SavesViewModelTests' own FakeGameProcessChecker — returns each answer in responses in order, then repeats the last one, so a test can say "not running at the first check, running by the late one".</summary>
    private sealed class FakeGameProcessChecker(params bool[] responses) : IGameProcessChecker
    {
        private int _index;

        public int CallCount { get; private set; }

        public bool IsRunning()
        {
            CallCount++;
            if (responses.Length == 0)
            {
                return false;
            }

            var value = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return value;
        }
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
        public LibraryEntry ImportPaks(IReadOnlyList<string> pakFilePaths, string name, string? source = null, int? nexusModId = null, string? catalogEntryId = null) => throw new NotSupportedException("Not exercised by these tests.");
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
