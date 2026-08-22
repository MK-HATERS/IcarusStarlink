using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Catalog.Ue4ss;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IUnrealPakService _unrealPakService;
    private readonly IWeeklyChangeReportStore _weeklyChangeReportStore;
    private readonly ISteamInstallLocator _steamInstallLocator;
    private readonly ICredentialStore _credentialStore;
    private readonly INexusApiClient _nexusApiClient;
    private readonly INxmProtocolRegistrar _nxmProtocolRegistrar;
    private readonly IUe4ssLoaderInstallService _ue4ssLoaderInstallService;
    private readonly IUe4ssReleaseClient _ue4ssReleaseClient;
    private readonly IAppUpdateClient _appUpdateClient;
    private readonly HttpClient _httpClient;
    private readonly string _backupDirectory;
    private readonly string _dataOutputDirectory;
    private readonly string _logsDirectory;
    private readonly string _settingsFilePath;

    public string Title => "Settings";

    [ObservableProperty]
    private string? _icarusContentPath;

    [ObservableProperty]
    private string? _unrealPakExePath;

    [ObservableProperty]
    private string? _savedMessage;

    [ObservableProperty]
    private bool _isUpdatingDataFolder;

    [ObservableProperty]
    private string? _dataFolderStatusMessage;

    /// <summary>Non-null only while a local data.pak hash mismatch has actually been detected — see CheckForGameUpdateAsync.</summary>
    [ObservableProperty]
    private string? _gameDataOutdatedMessage;

    [ObservableProperty]
    private string _nexusApiKeyInput = "";

    /// <summary>Null when not signed in — e.g. "SomeUser (Premium)".</summary>
    [ObservableProperty]
    private string? _nexusSignedInAs;

    [ObservableProperty]
    private bool _isAuthorizingNexus;

    public bool CanAuthorizeNexus => !IsAuthorizingNexus;

    [ObservableProperty]
    private string? _nexusStatusMessage;

    [ObservableProperty]
    private bool _isNxmProtocolRegisteredToThisApp;

    public bool IsNxmProtocolNotRegistered => !IsNxmProtocolRegisteredToThisApp;

    [ObservableProperty]
    private string? _nxmProtocolStatusMessage;

    partial void OnIsNxmProtocolRegisteredToThisAppChanged(bool value) => OnPropertyChanged(nameof(IsNxmProtocolNotRegistered));

    [ObservableProperty]
    private bool _isUe4ssInstalled;

    [ObservableProperty]
    private string? _ue4ssInstalledVersion;

    /// <summary>Null until GetLatestStableReleaseAsync succeeds — offline/rate-limited/unreachable all leave this null, disabling Install/Update rather than guessing a download URL.</summary>
    [ObservableProperty]
    private Ue4ssReleaseInfo? _ue4ssLatestRelease;

    [ObservableProperty]
    private bool _isCheckingUe4ssRelease;

    [ObservableProperty]
    private bool _isInstallingUe4ss;

    [ObservableProperty]
    private string? _ue4ssStatusMessage;

    public string Ue4ssStatusText => (IsUe4ssInstalled, Ue4ssLatestRelease) switch
    {
        (false, _) => "Not installed.",
        (true, null) => $"v{Ue4ssInstalledVersion} installed.",
        (true, { } latest) when latest.Version == Ue4ssInstalledVersion => $"v{Ue4ssInstalledVersion} installed (up to date).",
        (true, { } latest) => $"v{Ue4ssInstalledVersion} installed — v{latest.Version} available.",
    };

    public string Ue4ssInstallButtonLabel => (IsUe4ssInstalled, Ue4ssLatestRelease) switch
    {
        (false, null) => "Install",
        (false, { } latest) => $"Install v{latest.Version}",
        (true, null) => "Update",
        (true, { } latest) when latest.Version == Ue4ssInstalledVersion => "Reinstall",
        (true, { } latest) => $"Update to v{latest.Version}",
    };

    public bool CanInstallOrUpdateUe4ss => Ue4ssLatestRelease is not null && !IsInstallingUe4ss;

    partial void OnIsUe4ssInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(Ue4ssStatusText));
        OnPropertyChanged(nameof(Ue4ssInstallButtonLabel));
    }

    partial void OnUe4ssInstalledVersionChanged(string? value) => OnPropertyChanged(nameof(Ue4ssStatusText));

    partial void OnUe4ssLatestReleaseChanged(Ue4ssReleaseInfo? value)
    {
        OnPropertyChanged(nameof(Ue4ssStatusText));
        OnPropertyChanged(nameof(Ue4ssInstallButtonLabel));
        OnPropertyChanged(nameof(CanInstallOrUpdateUe4ss));
    }

    partial void OnIsInstallingUe4ssChanged(bool value) => OnPropertyChanged(nameof(CanInstallOrUpdateUe4ss));

    public SettingsViewModel(
        ISettingsService settingsService, IUnrealPakService unrealPakService, IWeeklyChangeReportStore weeklyChangeReportStore,
        ISteamInstallLocator steamInstallLocator, ICredentialStore credentialStore, INexusApiClient nexusApiClient,
        INxmProtocolRegistrar nxmProtocolRegistrar, IUe4ssLoaderInstallService ue4ssLoaderInstallService,
        IUe4ssReleaseClient ue4ssReleaseClient, IAppUpdateClient appUpdateClient, HttpClient httpClient,
        string backupDirectory, string dataOutputDirectory, string logsDirectory, string settingsFilePath)
    {
        _settingsService = settingsService;
        _unrealPakService = unrealPakService;
        _weeklyChangeReportStore = weeklyChangeReportStore;
        _steamInstallLocator = steamInstallLocator;
        _credentialStore = credentialStore;
        _nexusApiClient = nexusApiClient;
        _nxmProtocolRegistrar = nxmProtocolRegistrar;
        _ue4ssLoaderInstallService = ue4ssLoaderInstallService;
        _ue4ssReleaseClient = ue4ssReleaseClient;
        _appUpdateClient = appUpdateClient;
        _httpClient = httpClient;
        _backupDirectory = backupDirectory;
        _dataOutputDirectory = dataOutputDirectory;
        _logsDirectory = logsDirectory;
        _settingsFilePath = settingsFilePath;
        _icarusContentPath = settingsService.Current.IcarusContentPath;
        _unrealPakExePath = settingsService.Current.UnrealPakExePath;
        _performanceTrackingEnabled = settingsService.Current.PerformanceTrackingEnabled;
        _isNxmProtocolRegisteredToThisApp = nxmProtocolRegistrar.IsRegisteredToThisApp();
        _hasSavedGitHubToken = credentialStore.Read(CredentialTargets.GitHubToken) is not null;

        if (!string.IsNullOrWhiteSpace(IcarusContentPath))
        {
            RefreshUe4ssStatus();
        }

        // Fire-and-forget, same shape as DownloadsViewModel's constructor-triggered
        // RefreshCatalogAsync: constructors can't be async, and CheckForGameUpdateAsync has its
        // own top-level try/catch so nothing here can produce an unobserved exception.
        _ = CheckForGameUpdateAsync();
        _ = InitializeNexusStatusAsync();
        _ = CheckUe4ssLatestReleaseAsync();
        _ = CheckForAppUpdatesOnLaunchAsync();
    }

    [RelayCommand]
    private void BrowseIcarusContentFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the Icarus\\Icarus\\Content folder",
        };

        if (dialog.ShowDialog() == true)
        {
            IcarusContentPath = dialog.FolderName;
        }
    }

    /// <summary>
    /// Phase 7.5: reads Steam's own install path from the registry, walks its real
    /// libraryfolders.vdf, and checks each library for Icarus's own App ID (1149460) —
    /// pre-fills the field but doesn't save on its own; the user still confirms via the
    /// existing Save button (or overrides it with Browse… first) just like a manual edit would.
    /// </summary>
    [RelayCommand]
    private void AutoDetectIcarusContentFolder()
    {
        var detected = _steamInstallLocator.FindIcarusContentPath();
        if (detected is null)
        {
            SavedMessage = "Couldn't find Icarus through Steam automatically — use Browse… instead.";
            return;
        }

        IcarusContentPath = detected;
        SavedMessage = "Found via Steam — click Save to keep it.";
    }

    [RelayCommand]
    private void BrowseUnrealPakExe()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select UnrealPak.exe",
            Filter = "UnrealPak.exe|UnrealPak.exe|Executable files (*.exe)|*.exe",
        };

        if (dialog.ShowDialog() == true)
        {
            UnrealPakExePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void Save()
    {
        _settingsService.Current.IcarusContentPath = IcarusContentPath;
        _settingsService.Current.UnrealPakExePath = UnrealPakExePath;
        SavedMessage = _settingsService.Save()
            ? $"Saved at {DateTime.Now:T}"
            : "Failed to save settings — check the logs.";
    }

    [RelayCommand]
    private async Task UpdateDataFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(IcarusContentPath) || string.IsNullOrWhiteSpace(UnrealPakExePath))
        {
            DataFolderStatusMessage = "Set both the Icarus Content folder and UnrealPak.exe path first.";
            return;
        }

        // Same paths this is about to extract with — save them now so a path typed but never
        // explicitly run through Save Settings still persists across restarts.
        Save();

        IsUpdatingDataFolder = true;
        DataFolderStatusMessage = "Extracting…";

        try
        {
            var previousUpdateAt = _settingsService.Current.LastDataFolderUpdatedAt;
            var result = await _unrealPakService.ExtractDataPakAsync(UnrealPakExePath, IcarusContentPath, _dataOutputDirectory, previousUpdateAt);

            if (result.ChangeReport is { } report)
            {
                _weeklyChangeReportStore.Save(report);
            }

            // Recorded regardless of whether a report was produced (also covers the first-ever
            // run, which has nothing to diff against yet) — this is what the *next* run's
            // previousUpdateAt, and CheckForGameUpdateAsync's own baseline, come from.
            _settingsService.Current.LastDataFolderUpdatedAt = DateTimeOffset.UtcNow;
            _settingsService.Current.LastDataPakHash = await _unrealPakService.TryGetDataPakHashAsync(IcarusContentPath);
            _settingsService.Save();
            GameDataOutdatedMessage = null;
            WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

            DataFolderStatusMessage = $"Extracted {result.ExtractedFileCount} files. {DescribeChangeReport(result.ChangeReport)}";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else in this app: a wrong path, a UnrealPak.exe that
            // can't run, or the game having moved/renamed data.pak should show a status message,
            // not crash the app.
            DataFolderStatusMessage = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsUpdatingDataFolder = false;
        }
    }

    private static string DescribeChangeReport(WeeklyChangeReport? report) => report switch
    {
        null => "This is your first update — nothing to compare yet.",
        { ChangedFiles.Count: 0 } => "No JSON changes since your last update.",
        var r => $"{r.ChangedFiles.Count} JSON file(s) changed since your last update — see Weekly Changes.",
    };

    /// <summary>
    /// A passive, local-only check (no network) — compares data.pak's current hash against the one
    /// recorded at the last successful Update data folder run. Only ever notifies; never
    /// re-extracts on its own. Classic IMM originally did auto-extract on this kind of detection
    /// and walked it back ("Weekly updates are no longer auto updated when you run the program. You
    /// will have to manually click the update data folder button when an update needs to be done."
    /// — from its own changelog), so this deliberately stops at telling the user, the same way
    /// classic IMM's own settled behavior does.
    /// </summary>
    private async Task CheckForGameUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(IcarusContentPath) || _settingsService.Current.LastDataPakHash is not { } lastKnownHash)
        {
            return;
        }

        var currentHash = await _unrealPakService.TryGetDataPakHashAsync(IcarusContentPath);
        if (currentHash is not null && currentHash != lastKnownHash)
        {
            GameDataOutdatedMessage = "Icarus has been updated since your last data refresh — click Update data folder to see what changed.";
        }
    }

    partial void OnIsAuthorizingNexusChanged(bool value) => OnPropertyChanged(nameof(CanAuthorizeNexus));

    [RelayCommand]
    private void OpenNexusApiKeyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.nexusmods.com/users/myaccount?tab=api") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening the default browser is best-effort UX, not a core operation — same
            // swallow-and-move-on DownloadsViewModel's own OpenUrl already uses.
        }
    }

    /// <summary>
    /// A real, live validation against Nexus's own API — "just like a new user would," per the
    /// user's own explicit ask, not a hardcoded test key. Only saves the key to Windows Credential
    /// Manager once Nexus itself has confirmed it's genuinely valid.
    /// </summary>
    [RelayCommand]
    private async Task AuthorizeNexusAsync()
    {
        if (string.IsNullOrWhiteSpace(NexusApiKeyInput))
        {
            NexusStatusMessage = "Paste your API key first.";
            return;
        }

        IsAuthorizingNexus = true;
        NexusStatusMessage = null;
        try
        {
            var trimmedKey = NexusApiKeyInput.Trim();
            var user = await _nexusApiClient.ValidateKeyAsync(trimmedKey);
            if (user is null)
            {
                NexusStatusMessage = "Nexus didn't accept that key — double check you copied the whole thing.";
                return;
            }

            _credentialStore.Save(CredentialTargets.NexusApiKey, trimmedKey);
            NexusSignedInAs = user.IsPremium ? $"{user.Name} (Premium)" : user.Name;
            NexusApiKeyInput = "";
            NexusStatusMessage = "Signed in.";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a network hiccup or Nexus itself being down
            // shows a status message, distinguishable from "that key is wrong" (the null case
            // above) since this is the genuine-failure path, not a rejected key.
            NexusStatusMessage = $"Couldn't reach Nexus: {ex.Message}";
        }
        finally
        {
            IsAuthorizingNexus = false;
        }
    }

    [RelayCommand]
    private void SignOutNexus()
    {
        _credentialStore.Delete(CredentialTargets.NexusApiKey);
        NexusSignedInAs = null;
        NexusStatusMessage = "Signed out.";
    }

    /// <summary>
    /// Re-validates a previously-saved key once per launch (not on every Settings visit) — cheap
    /// against Nexus's own generous rate limit (500/day), and catches a since-revoked key early
    /// rather than only surfacing that the next time the user happens to click Authorize again.
    /// </summary>
    private async Task InitializeNexusStatusAsync()
    {
        var storedKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (storedKey is null)
        {
            return;
        }

        try
        {
            var user = await _nexusApiClient.ValidateKeyAsync(storedKey);
            if (user is null)
            {
                NexusStatusMessage = "Your saved Nexus API key is no longer valid — sign in again below.";
                return;
            }

            NexusSignedInAs = user.IsPremium ? $"{user.Name} (Premium)" : user.Name;
        }
        catch (Exception)
        {
            // Best-effort startup check — a network hiccup shouldn't block Settings from loading
            // or claim the saved key is invalid when it might still be fine; Authorize will surface
            // a clearer error if the user tries it manually.
            NexusStatusMessage = "Couldn't reach Nexus to confirm your saved key — it may still be valid.";
        }
    }

    /// <summary>
    /// Phase 8.3c: a real, hard-to-reverse registry write (this app becomes the OS's nxm:// handler
    /// for this Windows account), so this is the one place it can happen — never automatic, and
    /// gated behind the user's own explicit Yes in the confirmation dialog below, not just having
    /// clicked the button in the first place.
    /// </summary>
    [RelayCommand]
    private void RegisterNxmProtocol()
    {
        var result = MessageBox.Show(
            "This registers IcarusStarlink as the handler for nxm:// links (Nexus's \"Mod Manager Download\" buttons) for your Windows account.\n\n" +
            "If another mod manager (e.g. Vortex) currently handles these, it will be replaced — you can switch back with Unregister below, or by re-registering that other app.\n\n" +
            "Continue?",
            "Register as Nexus download handler", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _nxmProtocolRegistrar.Register();
            IsNxmProtocolRegisteredToThisApp = _nxmProtocolRegistrar.IsRegisteredToThisApp();
            NxmProtocolStatusMessage = IsNxmProtocolRegisteredToThisApp
                ? "Registered — Nexus \"Mod Manager Download\" buttons now open IcarusStarlink."
                : "Registration didn't take effect — try again, or check that nothing is blocking registry writes.";
        }
        catch (Exception ex)
        {
            NxmProtocolStatusMessage = $"Registration failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void UnregisterNxmProtocol()
    {
        try
        {
            _nxmProtocolRegistrar.Unregister();
            IsNxmProtocolRegisteredToThisApp = _nxmProtocolRegistrar.IsRegisteredToThisApp();
            NxmProtocolStatusMessage = "Unregistered.";
        }
        catch (Exception ex)
        {
            NxmProtocolStatusMessage = $"Couldn't unregister: {ex.Message}";
        }
    }

    private void RefreshUe4ssStatus()
    {
        var status = _ue4ssLoaderInstallService.GetStatus(IcarusContentPath!);
        IsUe4ssInstalled = status.IsInstalled;
        Ue4ssInstalledVersion = status.InstalledVersion;
    }

    // Only Browse…/Auto-detect route through this — the constructor assigns the backing field
    // directly (bypassing the generated setter, and this hook with it) before calling
    // RefreshUe4ssStatus() itself further down, so there's no double-call on startup. Without this,
    // pointing Settings at a game install that already has UE4SS kept showing "Not installed" until
    // the user clicked Install anyway or restarted the app.
    partial void OnIcarusContentPathChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            RefreshUe4ssStatus();
        }
    }

    /// <summary>Best-effort, once per launch — offline or GitHub-unreachable just leaves Ue4ssLatestRelease null (Install/Update stays disabled) rather than surfacing an error nobody asked for.</summary>
    private async Task CheckUe4ssLatestReleaseAsync()
    {
        IsCheckingUe4ssRelease = true;
        try
        {
            Ue4ssLatestRelease = await _ue4ssReleaseClient.GetLatestStableReleaseAsync();
        }
        finally
        {
            IsCheckingUe4ssRelease = false;
        }
    }

    /// <summary>
    /// Phase 8.5: a real, hard-to-reverse write into Binaries\Win64 — the game's own executable
    /// folder, more sensitive than either target this app has written to before (Content\Paks\mods,
    /// the UE4SS Mods folder). Same gating philosophy as the real pak install and the nxm://
    /// protocol registration: never automatic, and only proceeds behind this explicit confirmation.
    /// </summary>
    [RelayCommand]
    private async Task InstallOrUpdateUe4ssAsync()
    {
        if (string.IsNullOrWhiteSpace(IcarusContentPath))
        {
            Ue4ssStatusMessage = "Set the Icarus Content folder first.";
            return;
        }

        if (Ue4ssLatestRelease is not { } release)
        {
            Ue4ssStatusMessage = "Couldn't reach GitHub to find the latest release — try again.";
            return;
        }

        var wasInstalled = IsUe4ssInstalled;
        var verb = wasInstalled ? "Update" : "Install";
        var result = MessageBox.Show(
            $"This downloads UE4SS v{release.Version} from GitHub and installs it into your game's Binaries\\Win64 folder " +
            "— the loader that lets Lua/scripting mods run.\n\n" +
            "Your existing UE4SS.dll/dwmapi.dll are backed up first (last 5 kept). Any mods already in your Mods folder, " +
            "and your own UE4SS-settings.ini if you've customized it, are left untouched.\n\n" +
            "Continue?",
            $"{verb} UE4SS", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        IsInstallingUe4ss = true;
        Ue4ssStatusMessage = "Downloading…";
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"UE4SS_{Guid.NewGuid():N}.zip");
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(release.DownloadUrl);
            await File.WriteAllBytesAsync(tempZipPath, bytes);

            Ue4ssStatusMessage = "Installing…";
            await _ue4ssLoaderInstallService.InstallOrUpdateAsync(IcarusContentPath, tempZipPath, _backupDirectory);

            RefreshUe4ssStatus();
            Ue4ssStatusMessage = $"{(wasInstalled ? "Updated" : "Installed")} to v{release.Version}.";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"{verb} failed: {ex.Message}";
        }
        finally
        {
            IsInstallingUe4ss = false;
            try
            {
                File.Delete(tempZipPath);
            }
            catch (Exception)
            {
                // Best-effort cleanup of a temp file — leaving a stray one behind isn't worth
                // surfacing over whatever the install itself already reported.
            }
        }
    }

    // --- Diagnostics & safety (Phase 9) ---
    [ObservableProperty]
    private bool _performanceTrackingEnabled;

    [ObservableProperty]
    private string? _diagnosticsStatusMessage;

    /// <summary>Saves immediately on toggle, matching Downloads' own column-visibility checkboxes — this isn't gated behind the Save Settings button, since a diagnostic on/off switch has no reason to wait.</summary>
    partial void OnPerformanceTrackingEnabledChanged(bool value)
    {
        _settingsService.Current.PerformanceTrackingEnabled = value;
        _settingsService.Save();
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export diagnostics zip",
            FileName = $"IcarusStarlink-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "Zip archive (*.zip)|*.zip",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, dialog.FileName);
            DiagnosticsStatusMessage = $"Exported to '{dialog.FileName}'.";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a locked log file or a full disk shows a
            // status message, not a crash, even for a feature whose whole purpose is helping
            // diagnose a crash.
            DiagnosticsStatusMessage = $"Export failed: {ex.Message}";
        }
    }

    // --- App updates (Phase 9) ---

    /// <summary>The version baked into this build via the App csproj's own &lt;Version&gt; property — bump that alongside tagging a real GitHub release.</summary>
    public string InstalledAppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    [ObservableProperty]
    private string _gitHubTokenInput = "";

    /// <summary>Whether a token is currently saved — the token itself is never held in this ViewModel or read back for display, only its presence/absence.</summary>
    [ObservableProperty]
    private bool _hasSavedGitHubToken;

    public bool HasNoSavedGitHubToken => !HasSavedGitHubToken;

    partial void OnHasSavedGitHubTokenChanged(bool value) => OnPropertyChanged(nameof(HasNoSavedGitHubToken));

    [ObservableProperty]
    private bool _isCheckingForAppUpdate;

    public bool CanCheckForAppUpdates => !IsCheckingForAppUpdate;

    partial void OnIsCheckingForAppUpdateChanged(bool value) => OnPropertyChanged(nameof(CanCheckForAppUpdates));

    /// <summary>Null until CheckForAppUpdatesAsync/CheckForAppUpdatesOnLaunchAsync succeeds — offline/rate-limited/no-token-on-a-private-repo all leave this null.</summary>
    [ObservableProperty]
    private AppUpdateRelease? _latestAppUpdateRelease;

    [ObservableProperty]
    private string? _appUpdateStatusMessage;

    public bool IsAppUpdateAvailable =>
        LatestAppUpdateRelease is { } release
        && Version.TryParse(release.Version, out var latest)
        && Version.TryParse(InstalledAppVersion, out var installed)
        && latest > installed;

    partial void OnLatestAppUpdateReleaseChanged(AppUpdateRelease? value) => OnPropertyChanged(nameof(IsAppUpdateAvailable));

    [RelayCommand]
    private void SaveGitHubToken()
    {
        if (string.IsNullOrWhiteSpace(GitHubTokenInput))
        {
            AppUpdateStatusMessage = "Paste a GitHub personal access token first.";
            return;
        }

        _credentialStore.Save(CredentialTargets.GitHubToken, GitHubTokenInput.Trim());
        HasSavedGitHubToken = true;
        GitHubTokenInput = "";
        AppUpdateStatusMessage = "GitHub token saved.";
    }

    [RelayCommand]
    private void ClearGitHubToken()
    {
        _credentialStore.Delete(CredentialTargets.GitHubToken);
        HasSavedGitHubToken = false;
        AppUpdateStatusMessage = "GitHub token cleared.";
    }

    [RelayCommand]
    private async Task CheckForAppUpdatesAsync()
    {
        IsCheckingForAppUpdate = true;
        AppUpdateStatusMessage = null;
        try
        {
            var token = _credentialStore.Read(CredentialTargets.GitHubToken);
            LatestAppUpdateRelease = await _appUpdateClient.GetLatestReleaseAsync(token);
            AppUpdateStatusMessage = LatestAppUpdateRelease switch
            {
                null => "Couldn't check for updates — while the repo is private, a GitHub token above is required.",
                { } r when IsAppUpdateAvailable => $"v{r.Version} is available (you have v{InstalledAppVersion}).",
                _ => "You're up to date.",
            };
        }
        finally
        {
            IsCheckingForAppUpdate = false;
        }
    }

    /// <summary>
    /// Opens the release's own GitHub page rather than downloading/installing automatically — the
    /// spec's own documented manual fallback. A fully automated download-extract-relaunch pipeline
    /// is real, hard-to-reverse surgery on this app's own running files; that's the Updater.exe
    /// project's eventual job, deliberately not built out in this pass.
    /// </summary>
    [RelayCommand]
    private void OpenLatestReleasePage()
    {
        if (LatestAppUpdateRelease is not { } release)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/MK-HATERS/IcarusStarlink/releases/tag/v{release.Version}") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening the default browser is best-effort UX, not a core operation — same
            // swallow-and-move-on OpenNexusApiKeyPage already uses.
        }
    }

    /// <summary>
    /// Silent once-per-launch check, same shape as InitializeNexusStatusAsync/CheckUe4ssLatestReleaseAsync
    /// — no nag if unconfigured (no token yet, while the repo is private). Only a genuine newer
    /// release prompts, via a real Yes/No, matching every other "ask before doing something visible"
    /// gate already established in this app.
    /// </summary>
    private async Task CheckForAppUpdatesOnLaunchAsync()
    {
        var token = _credentialStore.Read(CredentialTargets.GitHubToken);
        var release = await _appUpdateClient.GetLatestReleaseAsync(token);
        if (release is null)
        {
            return;
        }

        LatestAppUpdateRelease = release;
        if (!IsAppUpdateAvailable)
        {
            return;
        }

        var result = MessageBox.Show(
            $"IcarusStarlink v{release.Version} is available — you're on v{InstalledAppVersion}.\n\nOpen the release page to download it?",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            OpenLatestReleasePage();
        }
    }
}
