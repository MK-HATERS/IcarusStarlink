using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel
{
    // --- Diagnostics & safety (Phase 9) ---
    [ObservableProperty]
    private bool _performanceTrackingEnabled;

    /// <summary>Settings › Diagnostics › Advanced log: debug detail and web requests in the log file too.</summary>
    [ObservableProperty]
    private bool _advancedLogging;

    /// <summary>Takes effect at once, no restart; saved straight away like Performance tracking.</summary>
    partial void OnAdvancedLoggingChanged(bool value)
    {
        _settingsService.Current.AdvancedLogging = value;
        _settingsService.Current.AdvancedLoggingUntilUtc = value ? DateTimeOffset.UtcNow + LogDetail.AdvancedFor : null;
        _settingsService.Save();
        LogDetail.ApplyFromSettings(_settingsService);
        DiagnosticsStatusMessage = value
            ? $"Advanced log on until {(DateTimeOffset.Now + LogDetail.AdvancedFor):g}, then it goes back to Basic by itself."
            : "Basic log: what you did and what happened.";
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (LogDetail.OpenFolder() is { } ex)
        {
            DiagnosticsStatusMessage = $"Couldn't open the log folder: {ex.Message}";
        }
    }

    [ObservableProperty]
    private string? _diagnosticsStatusMessage;

    /// <summary>Saves immediately on toggle, matching Downloads' own column-visibility checkboxes — this isn't gated behind the Save Settings button, since a diagnostic on/off switch has no reason to wait.</summary>
    partial void OnPerformanceTrackingEnabledChanged(bool value) =>
        _settingsService.SetAndSave(s => s.PerformanceTrackingEnabled, (s, v) => s.PerformanceTrackingEnabled = v, value);

    [RelayCommand]
    private void ExportDiagnostics()
    {
        if (_dialogService.PickSaveFile(
                "Export diagnostics zip", "Zip archive (*.zip)|*.zip", $"IcarusStarlink-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip") is not { } zipPath)
        {
            return;
        }

        try
        {
            DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, zipPath);
            DiagnosticsStatusMessage = $"Exported to '{zipPath}'.";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a locked log file or a full disk shows a
            // status message, not a crash, even for a feature whose whole purpose is helping
            // diagnose a crash.
            DiagnosticsStatusMessage = $"Export failed: {ex.Message}";
        }
    }

    // --- Backups folder size + Clean up backups ---

    /// <summary>"Backups: 1.6 GB" — the size of the app's own Backups folder, worked out off the UI thread.</summary>
    [ObservableProperty]
    private string _backupsSizeText = "Backups: …";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanUpBackupsCommand))]
    private bool _isCleaningBackups;

    [ObservableProperty]
    private string? _backupsStatusMessage;

    /// <summary>Measures the Backups folder again (off the UI thread); Settings calls it each time it's shown.</summary>
    public void RefreshBackupsSize() => _ = RefreshBackupsSizeAsync();

    private async Task RefreshBackupsSizeAsync()
    {
        try
        {
            var bytes = await Task.Run(() => BackupCleanup.MeasureBytes(_backupDirectory));
            BackupsSizeText = $"Backups: {BackupCleanup.FormatSize(bytes)}";
        }
        catch (Exception ex)
        {
            BackupsSizeText = "Backups: size unknown";
            BackupsStatusMessage = $"Couldn't measure the Backups folder: {ex.Message}";
        }
    }

    private bool CanCleanUpBackups() => !IsCleaningBackups;

    [RelayCommand(CanExecute = nameof(CanCleanUpBackups), FlowExceptionsToTaskScheduler = true)]
    private async Task CleanUpBackupsAsync()
    {
        if (!_dialogService.Confirm(
                $"Keep only the newest {BackupCleanup.KeptPerSetOnCleanUp} backups of each mod, the merged pak and each server, and delete the rest?\n\n" +
                "The newest backup of everything is always kept. Nothing outside the Backups folder is touched.",
                "Clean up backups", ThemedConfirmSeverity.Question))
        {
            return;
        }

        IsCleaningBackups = true;
        BackupsStatusMessage = "Cleaning up…";
        try
        {
            var result = await Task.Run(() => BackupCleanup.CleanUp(_backupDirectory));
            BackupsStatusMessage = result.FailedCount > 0
                ? $"Freed {BackupCleanup.FormatSize(result.FreedBytes)}. {result.FailedCount} backup(s) couldn't be deleted (in use?)."
                : $"Freed {BackupCleanup.FormatSize(result.FreedBytes)}.";
            _activityLog.Log($"Clean up backups: deleted {result.DeletedCount} old backup(s), freed {BackupCleanup.FormatSize(result.FreedBytes)}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            BackupsStatusMessage = $"Clean up failed: {ex.Message}";
        }
        finally
        {
            IsCleaningBackups = false;
        }

        await RefreshBackupsSizeAsync();
    }

    // --- App updates (Phase 9) ---

    /// <summary>The version baked into this build via the App csproj's own &lt;Version&gt; property — bump that alongside tagging a real GitHub release.</summary>
    public string InstalledAppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    [ObservableProperty]
    private bool _isCheckingForAppUpdate;

    public bool CanCheckForAppUpdates => !IsCheckingForAppUpdate;

    partial void OnIsCheckingForAppUpdateChanged(bool value) => OnPropertyChanged(nameof(CanCheckForAppUpdates));

    /// <summary>Null until CheckForAppUpdatesAsync/CheckForAppUpdatesOnLaunchAsync succeeds — offline or rate-limited both leave this null.</summary>
    [ObservableProperty]
    private AppUpdateRelease? _latestAppUpdateRelease;

    [ObservableProperty]
    private string? _appUpdateStatusMessage;

    /// <summary>What the last check offers (AppUpdateChoice.Classify): a newer release, a stable one ready while on a beta, or the way back to stable.</summary>
    public AppUpdateOffer AppUpdateOffer => AppUpdateChoice.Classify(InstalledAppVersion, LatestAppUpdateRelease, UpdateChannel == BetaChannel);

    public bool IsAppUpdateAvailable => AppUpdateOffer != AppUpdateOffer.None;

    public bool IsSwitchBackToStable => AppUpdateOffer == AppUpdateOffer.SwitchBackToStable;

    private bool IsStableReadyForBeta => AppUpdateOffer == AppUpdateOffer.StableReady;

    partial void OnLatestAppUpdateReleaseChanged(AppUpdateRelease? value) => RaiseAppUpdateOfferChanged();

    private void RaiseAppUpdateOfferChanged()
    {
        OnPropertyChanged(nameof(AppUpdateOffer));
        OnPropertyChanged(nameof(IsAppUpdateAvailable));
        OnPropertyChanged(nameof(IsSwitchBackToStable));
    }

    public const string StableChannel = "Stable";
    public const string BetaChannel = "Beta";

    public IReadOnlyList<string> UpdateChannelOptions { get; } = [StableChannel, BetaChannel];

    /// <summary>
    /// Settings › Maintenance › Update channel (AppSettings.UpdateChannel). Beta also offers betas
    /// (GitHub prereleases); Stable offers only releases, and while running a beta offers the
    /// newest one to switch back. Changing it checks again straight away.
    /// </summary>
    public string UpdateChannel
    {
        get => _settingsService.Current.UpdateChannel == BetaChannel ? BetaChannel : StableChannel;
        set
        {
            var channel = value == BetaChannel ? BetaChannel : StableChannel;
            if (channel == UpdateChannel)
            {
                return;
            }

            _settingsService.SetAndSave(s => s.UpdateChannel, (s, v) => s.UpdateChannel = v, channel);
            OnPropertyChanged();
            RaiseAppUpdateOfferChanged();
            CheckForAppUpdatesCommand.Execute(null);
        }
    }

    /// <summary>
    /// The release this channel offers (AppUpdateChoice.Pick); betas are only looked up on the Beta
    /// channel. Stable releases come from Starlink's Nexus page when a Nexus key is set (the newer of
    /// it and GitHub, Nexus on a tie), else from GitHub; betas always from GitHub.
    /// </summary>
    private async Task<AppUpdateRelease?> GetChannelReleaseAsync()
    {
        var betaChannel = UpdateChannel == BetaChannel;
        var fromGitHub = await _appUpdateClient.GetLatestReleaseAsync();
        var fromNexus = _credentialStore.Read(CredentialTargets.NexusApiKey) is { } apiKey
            ? await NexusAppUpdates.FindLatestStableAsync(_nexusApiClient, _appUpdateClient, apiKey)
            : null;
        var stable = NexusAppUpdates.PickStable(fromNexus, fromGitHub);
        var beta = betaChannel ? await _appUpdateClient.GetLatestBetaReleaseAsync() : null;
        return AppUpdateChoice.Pick(stable, beta, betaChannel);
    }

    /// <summary>Settings › Maintenance: where the Stable channel's updates come from.</summary>
    public string StableUpdateSourceText => _credentialStore.Read(CredentialTargets.NexusApiKey) is null
        ? "Stable updates come from GitHub. Sign in to Nexus (Nexus tab) to get them from Starlink's Nexus page instead."
        : "Stable updates come from Starlink's Nexus page (GitHub if Nexus doesn't answer); betas from GitHub.";

    /// <summary>Settings › Maintenance › Check for updates when Starlink starts (AppSettings.AutoCheckAppUpdates).</summary>
    public bool AutoCheckAppUpdates
    {
        get => _settingsService.Current.AutoCheckAppUpdates;
        set
        {
            if (_settingsService.SetAndSave(s => s.AutoCheckAppUpdates, (s, v) => s.AutoCheckAppUpdates = v, value))
            {
                OnPropertyChanged();
            }
        }
    }

    private static bool SameAppVersion(string a, string b) =>
        AppReleaseVersion.TryParse(a) is { } left && AppReleaseVersion.TryParse(b) is { } right
            ? left.CompareTo(right) == 0
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CheckForAppUpdatesAsync()
    {
        IsCheckingForAppUpdate = true;
        AppUpdateStatusMessage = null;
        try
        {
            LatestAppUpdateRelease = await GetChannelReleaseAsync();
            AppUpdateStatusMessage = LatestAppUpdateRelease switch
            {
                null => IcarusStarlink.Catalog.GitHub.GitHubApiCache.RateLimitMessage is { } limit
                    ? $"Couldn't check for updates. {limit}"
                    : "Couldn't check for updates: GitHub didn't answer (offline, or GitHub is down).",
                { } r when IsSwitchBackToStable => $"Stable v{r.Version} is available — Update now switches you back from beta v{InstalledAppVersion}.",
                { } r when IsStableReadyForBeta => $"Stable v{r.Version} is ready — you're on beta v{InstalledAppVersion}.",
                { } r when IsAppUpdateAvailable => $"v{r.Version} is available (you have v{InstalledAppVersion}).",
                _ => "You're up to date.",
            };
        }
        finally
        {
            IsCheckingForAppUpdate = false;
        }

        // A manual check covers UE4SS too (the answer shows on the UE4SS tab). Only here, never
        // on the automatic launch check.
        if (LatestAppUpdateRelease is not null)
        {
            await CheckForUe4ssUpdateAsync();
            if (IsUe4ssInstalled && Ue4ssInstallOrder == Ue4ssBuildOrder.Newer)
            {
                AppUpdateStatusMessage += $" UE4SS {Ue4ssInstallLabel} is also available (Settings > UE4SS).";
            }
        }
    }

    [ObservableProperty]
    private bool _isInstallingAppUpdate;

    /// <summary>
    /// Big-plan item 4 — the real automated in-place update: download the release zip, extract it,
    /// stage a TEMP copy of Updater.exe (so its own file in the install folder can be replaced
    /// too), hand off (this process's id, the install dir, the new files, the exe to relaunch),
    /// and exit. Updater.exe waits for this process to fully exit, copies the new build over the
    /// install directory WITHOUT deleting anything (user data is safe by construction — see
    /// UpdateApplier), and relaunches the app. Gated behind its own explicit Yes/No, like every
    /// other self-affecting action in this app.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task UpdateNowAsync()
    {
        if (LatestAppUpdateRelease is null)
        {
            return;
        }

        var confirmed = _dialogService.Confirm(
            (IsSwitchBackToStable ? $"Switch back from beta v{InstalledAppVersion} to stable v{LatestAppUpdateRelease.Version}?" : $"Download and install v{LatestAppUpdateRelease.Version} now?")
            + "\n\nThe app will close, update itself, and reopen. Your mods, profiles, and settings are not touched.",
            "Install update", ThemedConfirmSeverity.Question);
        if (confirmed)
        {
            await InstallAppUpdateAsync();
        }
    }

    private async Task InstallAppUpdateAsync()
    {
        if (LatestAppUpdateRelease is not { } release || IsInstallingAppUpdate)
        {
            return;
        }

        // Shutdown() below would cut a running extraction or UE4SS install off midway (an orphaned
        // UnrealPak process, a half-written Data folder) — an audit finding.
        if (IsUpdatingDataFolder || IsInstallingUe4ss || IsUninstallingUe4ss)
        {
            AppUpdateStatusMessage = "Wait for the current data extraction / UE4SS change to finish, then click Update again.";
            return;
        }

        // The same for the Server page: closing mid-way would leave a half-updated server, a
        // server automation had stopped and not yet started again, or a half-uploaded pak.
        if (DescribeBackgroundWork() is { } busy)
        {
            AppUpdateStatusMessage = busy;
            return;
        }

        RunUpdateWindow(release, nexusLink: null);
    }

    /// <summary>
    /// The update window: download (Nexus or GitHub), check, unpack, hand over, with progress and
    /// Cancel. Closes Starlink once the updater has it.
    /// </summary>
    private void RunUpdateWindow(AppUpdateRelease release, NxmUrl? nexusLink)
    {
        IsInstallingAppUpdate = true;
        try
        {
            var viewModel = new AppUpdateViewModel(release, _appUpdateClient, _nexusApiClient, _credentialStore.Read(CredentialTargets.NexusApiKey),
                DescribeBackgroundWork, _settingsService.Current.ExeRenameDone, url => UrlOpener.TryOpen(url), nexusLink);
            new AppUpdateWindow(Application.Current.MainWindow, viewModel).ShowDialog();
            AppUpdateStatusMessage = viewModel.HandedOver ? "Installing the update…" : viewModel.ErrorText;
            if (viewModel.HandedOver)
            {
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            AppUpdateStatusMessage = $"Update failed before anything was changed: {ex.Message}";
        }
        finally
        {
            IsInstallingAppUpdate = false;
        }
    }

    /// <summary>
    /// A Mod Manager Download from Starlink's own Nexus page that no update window was waiting for
    /// (App.HandleNxmUrl): it's Starlink itself, so it's offered as an update rather than imported
    /// as a mod.
    /// </summary>
    public async Task OfferUpdateFromNexusLinkAsync(string nxmUrl)
    {
        if (IsInstallingAppUpdate || _credentialStore.Read(CredentialTargets.NexusApiKey) is not { } apiKey)
        {
            return;
        }

        NxmUrl link;
        try
        {
            link = NxmUrl.Parse(nxmUrl);
        }
        catch (FormatException)
        {
            return;
        }

        // Which version that file is, and its checksum from GitHub when it's there too.
        AppUpdateRelease? release = null;
        try
        {
            var file = (await _nexusApiClient.GetModFilesAsync(apiKey, NxmHandlerCommand.IcarusDomain, NexusAppUpdates.StarlinkModId))
                .FirstOrDefault(f => f.FileId == link.FileId);
            if (file is not null && AppReleaseVersion.TryParse(file.Version) is { } version)
            {
                var onGitHub = await _appUpdateClient.GetReleaseAsync(version.ToString());
                release = new AppUpdateRelease(version.ToString(), onGitHub?.ReleaseNotes ?? file.Notes ?? "", onGitHub?.AssetBrowserDownloadUrl ?? "",
                    onGitHub?.AssetDigest, file.FileId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nexus didn't say which file it is: asked about below without a version.
        }

        if (release is null)
        {
            AppUpdateStatusMessage = "That Nexus download is Icarus Starlink itself, but Nexus didn't say which version it is — use Check for updates instead.";
            return;
        }

        var same = SameAppVersion(release.Version, InstalledAppVersion);
        if (!_dialogService.Confirm(
                (same ? $"That Nexus download is Icarus Starlink v{release.Version}, the version you have. Install it again?" : $"That Nexus download is Icarus Starlink v{release.Version} (you have v{InstalledAppVersion}). Install it as an update?")
                + "\n\nThe app will close, update itself, and reopen. Your mods, profiles, and settings are not touched.",
                "Install Icarus Starlink", ThemedConfirmSeverity.Question))
        {
            return;
        }

        RunUpdateWindow(release, link);
    }

    /// <summary>"Wait until SteamCMD is updating a server has finished…" when a page has work that closing the app would cut off; null when it is safe to close.</summary>
    private static string? DescribeBackgroundWork()
    {
        if (AppBusyCheck.DescribeUnsaved() is { } unsaved)
        {
            return $"Save or reload {unsaved} first (updating closes the app), then click Update again.";
        }

        return AppBusyCheck.DescribeRunning() is { } running ? $"Wait until {running} has finished, then click Update again." : null;
    }

    /// <summary>
    /// Once, at launch: the updater leaves a note before it relaunches the app. A rolled-back or
    /// failed update used to bring the old version back without a word; now it says what happened
    /// (a warning also pops up), and a good update says so too.
    /// </summary>
    private void ReportLastAppUpdateOutcome()
    {
        var outcome = AppUpdateOutcome.TakeLast(
            Path.Combine(_logsDirectory, AppUpdateOutcome.FileName),
            Path.Combine(Path.GetTempPath(), AppUpdateOutcome.TempFileName));
        if (outcome is null)
        {
            return;
        }

        if (outcome.Installed)
        {
            AppUpdateStatusMessage = $"Updated to v{InstalledAppVersion}.";
            _activityLog.Log($"Icarus Starlink was updated to v{InstalledAppVersion}.", ActivityEntryKind.Success);
        }
        else
        {
            AppUpdateStatusMessage = outcome.Message;
            _activityLog.Log($"The last Icarus Starlink update didn't install. {outcome.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>The manual fallback next to the automated Update now — opens the release's own GitHub page in the browser.</summary>
    [RelayCommand]
    private void OpenLatestReleasePage()
    {
        if (LatestAppUpdateRelease is not { } release)
        {
            return;
        }

        UrlOpener.TryOpen($"https://github.com/MK-HATERS/IcarusStarlink/releases/tag/v{release.Version}");
    }

    /// <summary>Always available, unlike OpenLatestReleasePage above — doesn't need a check to have run first, since the repo is public and its whole release history is just a normal GitHub page.</summary>
    [RelayCommand]
    private void OpenReleasesPage() => UrlOpener.TryOpen("https://github.com/MK-HATERS/IcarusStarlink/releases");

    /// <summary>
    /// Silent once-per-launch check, same shape as InitializeNexusStatusAsync
    /// — no nag on a transient failure (offline, rate-limited). Only a genuine newer release
    /// prompts, via a real Yes/No, matching every other "ask before doing something visible" gate
    /// already established in this app.
    /// </summary>
    private async Task CheckForAppUpdatesAfterSetupAsync()
    {
        try
        {
            await FirstLaunchSetupTask;
        }
        catch (Exception)
        {
            // RunFirstLaunchSetupAsync has its own catch-all; nothing to add here.
        }

        await CheckForAppUpdatesOnLaunchAsync();
    }

    private async Task CheckForAppUpdatesOnLaunchAsync()
    {
        if (!AutoCheckAppUpdates)
        {
            return;
        }

        var release = await GetChannelReleaseAsync();
        if (release is null)
        {
            return;
        }

        LatestAppUpdateRelease = release;

        // "What's new" — GitHub's own latest release matches the exact version we're running,
        // meaning the app itself was updated since we last recorded a seen version. Deliberately
        // silent when LastSeenAppVersion is still null (a fresh install, or an existing user's
        // first launch after this field was introduced) — only a genuine detected version change
        // shows the dialog, so it never appears out of nowhere on an unrelated launch.
        if (SameAppVersion(release.Version, InstalledAppVersion))
        {
            var settings = _settingsService.Current;
            if (settings.LastSeenAppVersion is not null
                && !string.Equals(settings.LastSeenAppVersion, InstalledAppVersion, StringComparison.OrdinalIgnoreCase))
            {
                await StartupPrompts.ShowAsync(() => new WhatsNewWindow(release.Version, release.ReleaseNotes) { Owner = Application.Current.MainWindow });
            }

            if (!string.Equals(settings.LastSeenAppVersion, InstalledAppVersion, StringComparison.OrdinalIgnoreCase))
            {
                settings.LastSeenAppVersion = InstalledAppVersion;
                _settingsService.Save();
            }

            return;
        }

        // A beta installed by hand keeps the Stable channel, so asking at every launch to go back
        // to a lower stable release would nag forever, one click from a downgrade (review,
        // 2026-10-23). Settings › Maintenance still shows the switch-back.
        if (!IsAppUpdateAvailable || IsSwitchBackToStable)
        {
            return;
        }

        var result = _dialogService.Confirm(
            (IsStableReadyForBeta
                ? $"Stable v{release.Version} is ready — you're on beta v{InstalledAppVersion}.\n\nInstall the stable release now?"
                : $"IcarusStarlink v{release.Version} is available — you're on v{InstalledAppVersion}.\n\nDownload and install it now?")
            + " The app will close, update itself, and reopen — your mods, profiles, and settings are not touched.",
            "Update available", ThemedConfirmSeverity.Information);
        if (result)
        {
            await InstallAppUpdateAsync();
        }
    }
}
