using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Catalog.Ue4ss;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Core.GameHealth;
using IcarusStarlink.Core.Migration;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Skins;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

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
    private readonly IThemeService _themeService;
    private readonly ICustomSkinStore _customSkinStore;
    private readonly ImmMigrationService _immMigrationService;
    private readonly IUnrealPakInstaller _unrealPakInstaller;
    private readonly IActivityLog _activityLog;
    private readonly IDialogService _dialogService;
    private readonly IGameProcessChecker _gameProcessChecker;
    private readonly IGameDataFreshness _gameDataFreshness;
    private readonly string _stagedUe4ssDirectory;

    /// <summary>Resolved lazily (not injected directly) purely to avoid forcing Merge &amp; Install's own construction — with its Library scan and catalog wiring — every time Settings is opened.</summary>
    private readonly Func<MergeInstallViewModel> _mergeInstallViewModel;
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
    [NotifyCanExecuteChangedFor(nameof(UpdateDataFolderCommand))]
    private bool _isUpdatingDataFolder;

    /// <summary>The extraction in progress, so a second caller waits for it instead of starting another into the same folder.</summary>
    private Task _dataFolderUpdate = Task.CompletedTask;

    [ObservableProperty]
    private string? _dataFolderStatusMessage;

    /// <summary>True only right after an UpdateDataFolderAsync run that actually found real
    /// changed files — drives a "View Weekly Changes" shortcut next to the status message instead
    /// of making the user find the nav item themselves.</summary>
    [ObservableProperty]
    private bool _hasWeeklyChanges;

    /// <summary>Non-null only while a local data.pak hash mismatch has actually been detected — see CheckForGameUpdateAsync.</summary>
    [ObservableProperty]
    private string? _gameDataOutdatedMessage;

    /// <summary>Shown in the "Update data folder" Expander's collapsed header — reads straight from settings rather than caching, since the only thing that changes it (UpdateDataFolderAsync) already explicitly notifies this property when it does.</summary>
    public string DataFolderSummary => _settingsService.Current.LastDataFolderUpdatedAt is { } updatedAt
        ? $"last updated {updatedAt.ToLocalTime():yyyy-MM-dd}"
        : "never updated";

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

    /// <summary>The other app that has nxm:// links now ("Vortex"), or null when it's Starlink or nothing — the header and the Take them back button say it.</summary>
    [ObservableProperty]
    private string? _nxmHandlerOwner;

    /// <summary>Settings › Nexus › Take them back automatically (AppSettings.KeepNxmHandler; NxmHandlerWatch acts on it).</summary>
    public bool KeepNxmHandler
    {
        get => _settingsService.Current.KeepNxmHandler;
        set
        {
            if (_settingsService.SetAndSave(s => s.KeepNxmHandler, (s, v) => s.KeepNxmHandler = v, value))
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Settings › Nexus › "Links for other games": the mod managers Starlink replaced and the games each one is passed.</summary>
    public ObservableCollection<NxmOtherAppRow> NxmOtherApps { get; } = [];

    public bool HasNxmOtherApps => NxmOtherApps.Count > 0;

    [RelayCommand]
    private void RemoveNxmOtherApp(NxmOtherAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            _nxmProtocolRegistrar.RemoveOtherApp(row.Command);
        }
        catch (Exception ex)
        {
            NxmProtocolStatusMessage = $"Couldn't remove it: {ex.Message}";
        }

        RefreshNxmHandlerState();
    }

    private void RefreshNxmHandlerState()
    {
        IsNxmProtocolRegisteredToThisApp = _nxmProtocolRegistrar.IsRegisteredToThisApp();
        var current = IsNxmProtocolRegisteredToThisApp ? null : _nxmProtocolRegistrar.CurrentHandlerCommand();
        NxmHandlerOwner = current is null ? null : NxmHandlerWatch.HandlerName(current);

        NxmOtherApps.Clear();
        try
        {
            var previous = _nxmProtocolRegistrar.PreviousHandlerCommand();
            foreach (var app in _nxmProtocolRegistrar.OtherApps())
            {
                NxmOtherApps.Add(new NxmOtherAppRow(app, string.Equals(app.Command, previous, StringComparison.OrdinalIgnoreCase), _nxmProtocolRegistrar));
            }
        }
        catch (Exception)
        {
            // An unreadable list just shows empty; routing falls back to the app replaced last.
        }

        OnPropertyChanged(nameof(HasNxmOtherApps));
        OnPropertyChanged(nameof(NxmHandlerHeaderState));
        OnPropertyChanged(nameof(NxmRegisterButtonText));
    }

    public string NxmHandlerHeaderState =>
        IsNxmProtocolRegisteredToThisApp ? "Registered to this app"
        : NxmHandlerOwner is { } owner ? $"{owner} has them now"
        : "Not set up (optional)";

    public string NxmRegisterButtonText => NxmHandlerOwner is { } owner ? $"Take them back from {owner}" : "Register";

    [ObservableProperty]
    private bool _isUe4ssInstalled;

    [ObservableProperty]
    private string? _ue4ssInstalledVersion;

    /// <summary>The UE4SS config tab (UE4SS-settings.ini for the game and Starlink's servers). Set by the app; null in tests that don't need it.</summary>
    public Ue4ssConfigViewModel? Ue4ssConfig { get; init; }

    /// <summary>The installed build's git commit (from UE4SS.log or Starlink's install marker): what tells "3.0.1" builds apart.</summary>
    [ObservableProperty]
    private string? _ue4ssInstalledCommit;

    /// <summary>The newest UE4SS dev build on GitHub. Null until the user presses Check for updates (never checked on its own) or when GitHub didn't answer; Install then uses the bundled build.</summary>
    [ObservableProperty]
    private Ue4ssReleaseInfo? _ue4ssLatestRelease;

    /// <summary>How the build Install would put in compares with the installed one, as worked out by the last manual check (Unknown before one).</summary>
    [ObservableProperty]
    private Ue4ssBuildOrder _ue4ssCheckedOrder;

    [ObservableProperty]
    private bool _hasCheckedUe4ssRelease;

    [ObservableProperty]
    private bool _isCheckingUe4ssRelease;

    [ObservableProperty]
    private bool _isInstallingUe4ss;

    /// <summary>
    /// Install/Update and Uninstall both write into the exact same real game files (dwmapi.dll,
    /// Binaries\Win64\ue4ss\), so they must never run concurrently with each other OR with
    /// themselves (a double-click) — CanInstallOrUpdateUe4ss/CanUninstallUe4ss both check both this
    /// AND IsInstallingUe4ss for real mutual exclusion in both directions.
    /// </summary>
    [ObservableProperty]
    private bool _isUninstallingUe4ss;

    [ObservableProperty]
    private string? _ue4ssStatusMessage;

    /// <summary>The installed build in words: "3.0.1 dev build 1c1a1497", "3.0.1 (official release)", or "3.0.1 dev build 1181" when it's a build Starlink knows the number of.</summary>
    public string Ue4ssInstalledLabel => Ue4ssBuild.Describe(Ue4ssInstalledVersion, Ue4ssInstalledCommit, KnownBuildNumber(Ue4ssInstalledCommit));

    public string Ue4ssStatusText
    {
        get
        {
            if (!IsUe4ssInstalled)
            {
                return $"Not installed. Starlink comes with UE4SS {BundledUe4ss.Label}.";
            }

            if (Ue4ssInstalledVersion is null)
            {
                return "Installed (its build shows once the game has started with it).";
            }

            return Ue4ssInstallOrder switch
            {
                Ue4ssBuildOrder.Same when HasCheckedUe4ssRelease => $"{Ue4ssInstalledLabel} installed (up to date).",
                Ue4ssBuildOrder.Newer => $"{Ue4ssInstalledLabel} installed — {Ue4ssInstallLabel} available.",
                Ue4ssBuildOrder.Older => $"{Ue4ssInstalledLabel} installed (newer than {Ue4ssInstallLabel}).",
                Ue4ssBuildOrder.Unknown when HasCheckedUe4ssRelease => $"{Ue4ssInstalledLabel} installed. The newest is {Ue4ssInstallLabel}.",
                _ => $"{Ue4ssInstalledLabel} installed.",
            };
        }
    }

    /// <summary>Install uses the UE4SS built into Starlink (no download) unless a manual check found a newer dev build on GitHub.</summary>
    public string Ue4ssInstallButtonLabel => !IsUe4ssInstalled
        ? $"Install {Ue4ssInstallLabel}"
        : Ue4ssInstallOrder switch
        {
            Ue4ssBuildOrder.Same => "Reinstall",
            Ue4ssBuildOrder.Newer => $"Update to {Ue4ssInstallLabel}",
            Ue4ssBuildOrder.Older => $"Install {Ue4ssInstallLabel} (older)",
            _ => $"Install {Ue4ssInstallLabel}",
        };

    /// <summary>The build Install would put in, in words.</summary>
    private string Ue4ssInstallLabel => UseGitHubUe4ss
        ? Ue4ssBuild.Describe(Ue4ssLatestRelease!.Version, Ue4ssLatestRelease.Commit, Ue4ssLatestRelease.BuildNumber)
        : BundledUe4ss.Label;

    private string Ue4ssInstallVersion => UseGitHubUe4ss ? Ue4ssLatestRelease!.Version : BundledUe4ss.Version;

    private string Ue4ssInstallCommit => UseGitHubUe4ss ? Ue4ssLatestRelease!.Commit! : BundledUe4ss.Commit;

    /// <summary>Download only when the manual check found a dev build other than the one inside Starlink (the bundled one was the newest when this Starlink was released, and experimental-latest only moves forward).</summary>
    private bool UseGitHubUe4ss => Ue4ssLatestRelease is { Commit: { } commit } && !Ue4ssBuild.SameCommit(commit, BundledUe4ss.Commit);

    /// <summary>How the build Install would put in compares with the installed one.</summary>
    private Ue4ssBuildOrder Ue4ssInstallOrder =>
        Ue4ssInstalledCommit is null ? Ue4ssBuildOrder.Unknown
        : Ue4ssBuild.SameCommit(Ue4ssInstalledCommit, Ue4ssInstallCommit) ? Ue4ssBuildOrder.Same
        // Every dev build comes after the v3.0.1 tag.
        : Ue4ssBuild.IsOfficialRelease(Ue4ssInstalledCommit) ? Ue4ssBuildOrder.Newer
        : Ue4ssCheckedOrder;

    private int? KnownBuildNumber(string? commit) =>
        Ue4ssBuild.SameCommit(commit, BundledUe4ss.Commit) ? BundledUe4ss.BuildNumber
        : Ue4ssLatestRelease is { Commit: { } latest, BuildNumber: { } number } && Ue4ssBuild.SameCommit(commit, latest) ? number
        : null;

    private void NotifyUe4ssLabels()
    {
        OnPropertyChanged(nameof(Ue4ssInstalledLabel));
        OnPropertyChanged(nameof(Ue4ssStatusText));
        OnPropertyChanged(nameof(Ue4ssInstallButtonLabel));
    }

    partial void OnUe4ssInstalledCommitChanged(string? value) => NotifyUe4ssLabels();

    partial void OnUe4ssCheckedOrderChanged(Ue4ssBuildOrder value) => NotifyUe4ssLabels();

    partial void OnHasCheckedUe4ssReleaseChanged(bool value) => NotifyUe4ssLabels();

    /// <summary>Never waits on GitHub: the bundled copy is always there.</summary>
    public bool CanInstallOrUpdateUe4ss => !IsInstallingUe4ss && !IsUninstallingUe4ss;

    public bool CanUninstallUe4ss => !IsInstallingUe4ss && !IsUninstallingUe4ss;

    partial void OnIsUe4ssInstalledChanged(bool value) => NotifyUe4ssLabels();

    partial void OnUe4ssInstalledVersionChanged(string? value) => NotifyUe4ssLabels();

    partial void OnUe4ssLatestReleaseChanged(Ue4ssReleaseInfo? value)
    {
        NotifyUe4ssLabels();
        OnPropertyChanged(nameof(CanInstallOrUpdateUe4ss));
    }

    partial void OnIsInstallingUe4ssChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallOrUpdateUe4ss));
        OnPropertyChanged(nameof(CanUninstallUe4ss));
    }

    partial void OnIsUninstallingUe4ssChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallOrUpdateUe4ss));
        OnPropertyChanged(nameof(CanUninstallUe4ss));
    }

    public SettingsViewModel(
        ISettingsService settingsService, IUnrealPakService unrealPakService, IWeeklyChangeReportStore weeklyChangeReportStore,
        ISteamInstallLocator steamInstallLocator, ICredentialStore credentialStore, INexusApiClient nexusApiClient,
        INxmProtocolRegistrar nxmProtocolRegistrar, IUe4ssLoaderInstallService ue4ssLoaderInstallService,
        IUe4ssReleaseClient ue4ssReleaseClient, IAppUpdateClient appUpdateClient, HttpClient httpClient,
        IThemeService themeService, ICustomSkinStore customSkinStore, ImmMigrationService immMigrationService,
        Func<MergeInstallViewModel> mergeInstallViewModel, IUnrealPakInstaller unrealPakInstaller, IActivityLog activityLog,
        IDialogService dialogService, IGameProcessChecker gameProcessChecker,
        string backupDirectory, string dataOutputDirectory, string logsDirectory, string settingsFilePath,
        string stagedUe4ssDirectory, IGameDataFreshness? gameDataFreshness = null)
    {
        // The same "is data.pak still the one extracted?" check Merge & Install asks (it was
        // written out twice); null builds the usual one.
        _gameDataFreshness = gameDataFreshness ?? new GameDataFreshness(unrealPakService, settingsService, () => this);
        _activityLog = activityLog;
        _dialogService = dialogService;
        _gameProcessChecker = gameProcessChecker;
        _unrealPakInstaller = unrealPakInstaller;
        _stagedUe4ssDirectory = stagedUe4ssDirectory;
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
        _themeService = themeService;
        _customSkinStore = customSkinStore;
        _immMigrationService = immMigrationService;
        _mergeInstallViewModel = mergeInstallViewModel;
        _backupDirectory = backupDirectory;
        _dataOutputDirectory = dataOutputDirectory;
        _logsDirectory = logsDirectory;
        _settingsFilePath = settingsFilePath;
        _icarusContentPath = settingsService.Current.IcarusContentPath;
        _unrealPakExePath = settingsService.Current.UnrealPakExePath;
        _performanceTrackingEnabled = settingsService.Current.PerformanceTrackingEnabled;
        _advancedLogging = settingsService.Current.AdvancedLogging;
        _showPopUpMessages = settingsService.Current.ShowPopUpMessages;
        // When Advanced switches itself back after its 24 hours, the switch shows it.
        LogDetail.SwitchedBackToBasic += () =>
        {
            _advancedLogging = false;
            OnPropertyChanged(nameof(AdvancedLogging));
        };
        RefreshNxmHandlerState();
        WeakReferenceMessenger.Default.Register<NxmHandlerChangedMessage>(this, (recipient, _) => ((SettingsViewModel)recipient).RefreshNxmHandlerState());

        RefreshUe4ssStatus();

        // Fire-and-forget, same shape as DownloadsViewModel's constructor-triggered
        // RefreshCatalogAsync: constructors can't be async. CheckForGameUpdateAsync can't throw:
        // TryGetDataPakHashAsync catches its own failures and answers null.
        _ = CheckForGameUpdateAsync();
        _ = InitializeNexusStatusAsync();
        // No UE4SS check here: GitHub is only asked about a newer UE4SS when the user presses
        // Check for updates (CheckForUe4ssUpdateAsync).

        // The Library's "UE4SS isn't installed" bar: install here, where the confirmation,
        // the game-running check and the backup already live. Refusals and failures also go to
        // the activity log (ReportUe4ssProblem), since this page usually isn't the one showing.
        WeakReferenceMessenger.Default.Register<InstallUe4ssRequestedMessage>(this, (recipient, message) =>
        {
            var install = ((SettingsViewModel)recipient).InstallOrUpdateUe4ssCommand.ExecuteAsync(null);
        });
        FirstLaunchSetupTask = RunFirstLaunchSetupAsync();
        // After setup, not alongside it (an audit finding): both show modal dialogs, and an
        // "Update available" accepted mid-setup shut the app down over a running data extraction.
        _ = CheckForAppUpdatesAfterSetupAsync();
        CheckForGameCrashesSinceLastInstall();
        ReportLastAppUpdateOutcome();
        _ = RefreshBackupsSizeAsync();

        LoadSkinTokens();
    }

    // --- Migrate from classic IMM ---

    [ObservableProperty]
    private bool _isMigratingFromImm;

    /// <summary>Computed bool rather than an inverting converter — the same pattern CanCheckForAppUpdates already uses, and the one this codebase settled on after repeated trouble with converter parameters.</summary>
    public bool CanMigrateFromImm => !IsMigratingFromImm;

    partial void OnIsMigratingFromImmChanged(bool value) => OnPropertyChanged(nameof(CanMigrateFromImm));

    [ObservableProperty]
    private string? _migrationStatusMessage;

    /// <summary>Optional — left empty, the classic IMM folder is worked out from wherever the picked mod list lives. Set it when your install isn't laid out that way (or just to be explicit).</summary>
    [ObservableProperty]
    private string? _immFolderPath;

    [RelayCommand]
    private void BrowseImmFolder()
    {
        if (_dialogService.PickFolder(ImmFolderPickerTitle) is not { } folder)
        {
            return;
        }

        ImmFolderPath = folder;
        MigrationStatusMessage = ImmInstallPaths.LooksLikeInstallRoot(folder)
            ? "Classic IMM folder set — now pick its mod list."
            : $"Careful: that folder has no Extracted_Mods + {ImmExtractedMods.FileName} in it, so it may not be a classic IMM install.";
    }

    private const string ImmFolderPickerTitle = "Select your classic IMM folder (the one containing Extracted_Mods)";

    public ObservableCollection<string> MigrationResults { get; } = [];

    public bool HasMigrationResults => MigrationResults.Count > 0;

    /// <summary>
    /// One-click migration: pick a classic IMM merge list, and every mod it names is copied out of
    /// IMM's own Extracted_Mods folder into this Library, linked to its Database or Nexus source
    /// where that can be determined, and finally queued in Merge &amp; Install in the list's own
    /// order — so the user's existing merge setup comes across whole instead of being rebuilt by
    /// hand.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportImmModListAsync()
    {
        if (_dialogService.PickFile(
                "Pick your classic IMM mod list (LastMergedMods.txt or IMM_Merged_Mod.txt)",
                "Mod list (*.txt, *.lst)|*.txt;*.lst|All files (*.*)|*.*") is not { } listPath)
        {
            return;
        }

        // An explicitly-chosen folder always wins — nobody's IMM install has to live where anyone
        // else's does, and auto-detection only works when the list happens to sit inside it.
        var installRoot = !string.IsNullOrWhiteSpace(ImmFolderPath) && ImmInstallPaths.LooksLikeInstallRoot(ImmFolderPath)
            ? ImmFolderPath
            : ImmInstallPaths.FindInstallRoot(listPath);

        if (installRoot is null)
        {
            // A list taken from the game's own Paks\mods folder has no IMM install anywhere near
            // it, so the mods themselves can't be found without being told where they live.
            MigrationStatusMessage = "That list isn't inside a classic IMM folder — pick your classic IMM folder so the mods can be found.";
            if (_dialogService.PickFolder(ImmFolderPickerTitle) is not { } pickedRoot)
            {
                MigrationStatusMessage = null;
                return;
            }

            if (!ImmInstallPaths.LooksLikeInstallRoot(pickedRoot))
            {
                MigrationStatusMessage = $"'{pickedRoot}' doesn't look like a classic IMM folder — it needs an Extracted_Mods folder and an {ImmExtractedMods.FileName} file.";
                return;
            }

            installRoot = pickedRoot;
        }

        // Shown back in the box so it's visible what was used, and reusable next time.
        ImmFolderPath = installRoot;

        IsMigratingFromImm = true;
        MigrationResults.Clear();
        OnPropertyChanged(nameof(HasMigrationResults));

        try
        {
            var progress = new Progress<string>(message => MigrationStatusMessage = message);
            var result = await _immMigrationService.MigrateAsync(listPath, installRoot, progress);

            foreach (var mod in result.Mods)
            {
                MigrationResults.Add(mod.Display);
            }

            OnPropertyChanged(nameof(HasMigrationResults));

            // The mods are here; now bring the merge list itself across, which is the half that
            // actually saves the user rebuilding their setup. Resolved directly rather than
            // announced by message: page ViewModels are constructed lazily on first navigation,
            // so a user who migrates before ever opening Merge & Install would have no instance
            // listening, and the queue would silently stay empty (found live).
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            var mergeInstall = _mergeInstallViewModel();
            mergeInstall.LoadModListFromFile(listPath);

            var linked = new List<string>();
            if (result.LinkedToDatabaseCount > 0)
            {
                linked.Add($"{result.LinkedToDatabaseCount} linked to the mod database");
            }

            if (result.LinkedToNexusCount > 0)
            {
                linked.Add($"{result.LinkedToNexusCount} linked to Nexus");
            }

            // "Not found" and "found but wouldn't import" are genuinely different problems with
            // different fixes (get the mod vs. fix/replace a broken one), so they're never merged
            // into one count — the failed ones also carry their real reason in the list below.
            var problems = new List<string>();
            if (result.MissingCount > 0)
            {
                problems.Add($"{result.MissingCount} not found in classic IMM's mods folder");
            }

            if (result.FailedCount > 0)
            {
                problems.Add($"{result.FailedCount} couldn't be imported");
            }

            MigrationStatusMessage =
                $"Brought over {result.ImportedCount} mod(s)"
                + (result.AlreadyPresentCount > 0 ? $", {result.AlreadyPresentCount} already here" : "")
                + (linked.Count > 0 ? $" ({string.Join(", ", linked)})" : "")
                + (problems.Count > 0 ? $" — {string.Join(", ", problems)}; see the list below." : ".")
                + " Your merge list is now queued in Merge & Install."
                + (result.FailedCatalogSources.Count > 0
                    ? $" ({string.Join(" and ", result.FailedCatalogSources)} unavailable — some mods may have missed a database link; try migrating again.)"
                    : "");
        }
        catch (Exception ex)
        {
            MigrationStatusMessage = $"Migration failed: {ex.Message}";
        }
        finally
        {
            IsMigratingFromImm = false;
        }
    }

    // --- Custom skin (big-plan item 6) ---

    /// <summary>Display order + plain-language description per token, so the editor reads as "what part of the UI is this" rather than a bare brush-key dump. Tokens the themes define but this list doesn't know still show (appended at the end, keyed name as its own description) — the editor can't silently drop a token added to the theme files later.</summary>
    private static readonly (string Key, string Description)[] SkinTokenOrder =
    [
        ("WindowBackgroundBrush", "Window background"),
        ("PanelBackgroundBrush", "Side panels and nav rail"),
        ("ContentBackgroundBrush", "Page content background"),
        ("CardBackgroundBrush", "Cards and input boxes"),
        ("CardRaisedBackgroundBrush", "Raised cards (one step lighter)"),
        ("ForegroundBrush", "Main text"),
        ("SecondaryForegroundBrush", "Secondary text"),
        ("FaintForegroundBrush", "Faint text and disabled items"),
        ("AccentBrush", "Accent — buttons and highlights"),
        ("AccentForegroundBrush", "Text on accent buttons"),
        ("AccentSoftBrush", "Soft accent fill (badges)"),
        ("BorderBrush", "Borders"),
        ("NavItemHoverBrush", "Nav item hover"),
        ("NavItemSelectedBrush", "Nav item selected"),
        ("DangerBrush", "Danger / removals"),
        ("DangerSoftBrush", "Soft danger fill (badges)"),
        ("SuccessBrush", "Success / additions"),
        ("SuccessSoftBrush", "Soft success fill (badges)"),
        ("InfoBrush", "Info"),
    ];

    public System.Collections.ObjectModel.ObservableCollection<SkinTokenViewModel> SkinTokens { get; } = [];

    [ObservableProperty]
    private string? _skinStatusMessage;

    private void LoadSkinTokens()
    {
        var defaults = _themeService.GetThemeColors("Icarus");
        var saved = _customSkinStore.Load()?.Colors ?? [];

        SkinTokens.Clear();
        foreach (var (key, description) in SkinTokenOrder)
        {
            if (!defaults.ContainsKey(key))
            {
                continue;
            }

            SkinTokens.Add(new SkinTokenViewModel(key, description)
            {
                Hex = saved.GetValueOrDefault(key) ?? defaults[key],
            });
        }

        foreach (var (key, hex) in defaults)
        {
            if (!SkinTokens.Any(t => t.Key == key))
            {
                SkinTokens.Add(new SkinTokenViewModel(key, key) { Hex = saved.GetValueOrDefault(key) ?? hex });
            }
        }
    }

    [RelayCommand]
    private void SaveSkin()
    {
        var invalid = SkinTokens.Where(t => !ThemeService.TryParseColor(t.Hex.Trim(), out _)).Select(t => t.Key).ToList();
        if (invalid.Count > 0)
        {
            SkinStatusMessage = $"Not saved — these aren't valid hex colors (#RRGGBB or #AARRGGBB): {string.Join(", ", invalid)}.";
            return;
        }

        try
        {
            _customSkinStore.Save(new CustomSkin
            {
                Colors = SkinTokens.ToDictionary(t => t.Key, t => t.Hex.Trim()),
            });

            if (_settingsService.Current.ThemeName == ThemeService.CustomThemeName)
            {
                _themeService.ApplyTheme(ThemeService.CustomThemeName);
                SkinStatusMessage = "Skin saved and applied.";
            }
            else
            {
                SkinStatusMessage = "Skin saved — pick the Custom theme (top right) to see it.";
            }
        }
        catch (Exception ex)
        {
            SkinStatusMessage = $"Couldn't save the skin: {ex.Message}";
        }
    }

    /// <summary>Fills the editor's boxes from a built-in theme's own values as a starting point — doesn't save or apply anything until Save skin is clicked.</summary>
    [RelayCommand]
    private void CopyThemeIntoSkin(string themeName)
    {
        var colors = _themeService.GetThemeColors(themeName);
        foreach (var token in SkinTokens)
        {
            if (colors.TryGetValue(token.Key, out var hex))
            {
                token.Hex = hex;
            }
        }

        SkinStatusMessage = $"Copied the {themeName} theme's values — adjust and Save skin.";
    }

    [RelayCommand]
    private void OpenSkinFile()
    {
        try
        {
            if (_customSkinStore.Load() is null)
            {
                _customSkinStore.Save(new CustomSkin
                {
                    Colors = SkinTokens.ToDictionary(t => t.Key, t => t.Hex.Trim()),
                });
            }

            if (UrlOpener.TryOpen(_customSkinStore.FilePath) is { } ex)
            {
                SkinStatusMessage = $"Couldn't open the skin file: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            SkinStatusMessage = $"Couldn't open the skin file: {ex.Message}";
        }
    }

    /// <summary>Re-reads the skin file into the editor (and re-applies it if Custom is active) — the companion to hand-editing via OpenSkinFile.</summary>
    [RelayCommand]
    private void ReloadSkinFile()
    {
        LoadSkinTokens();
        if (_settingsService.Current.ThemeName == ThemeService.CustomThemeName)
        {
            _themeService.ApplyTheme(ThemeService.CustomThemeName);
        }

        SkinStatusMessage = "Skin file reloaded.";
    }

    [RelayCommand]
    private void BrowseIcarusContentFolder()
    {
        if (_dialogService.PickFolder("Select the Icarus\\Icarus\\Content folder", IcarusContentPath) is { } folder)
        {
            IcarusContentPath = folder;
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
        SavedMessage = "Found via Steam — click Save settings to keep it.";
    }

    [RelayCommand]
    private void BrowseUnrealPakExe()
    {
        if (_dialogService.PickFile("Select UnrealPak.exe", "UnrealPak.exe|UnrealPak.exe|Executable files (*.exe)|*.exe") is { } exePath)
        {
            UnrealPakExePath = exePath;
            _ = VerifyUnrealPakAsync();
        }
    }

    // --- UnrealPak locate-or-install ---

    [ObservableProperty]
    private string? _unrealPakStatusMessage;

    /// <summary>
    /// Always true (see IUnrealPakInstaller.PayloadAvailable) — kept as the binding target for the
    /// install button's IsEnabled rather than removed, so a future build that genuinely can't offer
    /// an install (e.g. a sandboxed environment with no filesystem/network access at all) has
    /// somewhere to wire that in without touching the view.
    /// </summary>
    public bool CanInstallBundledUnrealPak => _unrealPakInstaller.PayloadAvailable;

    /// <summary>Label flips to Reinstall once the installed copy is what's configured — same button, and reinstalling IS the repair path for a broken copy. Says "UnrealPak" rather than "bundled copy" since installing it may mean downloading it, not just extracting something already shipped alongside the app.</summary>
    public string InstallUnrealPakButtonLabel =>
        string.Equals(UnrealPakExePath, _unrealPakInstaller.InstalledExePath, StringComparison.OrdinalIgnoreCase)
            ? "Reinstall UnrealPak"
            : "Install UnrealPak";

    partial void OnUnrealPakExePathChanged(string? value) => OnPropertyChanged(nameof(InstallUnrealPakButtonLabel));

    /// <summary>
    /// Really runs the exe (see UnrealPakInstaller.VerifyAsync) — deliberately NOT a "check for
    /// updates" against anything newer: UnrealPak is engine-version-specific, Icarus is a UE 4.27
    /// title, and a newer UE5 UnrealPak writes pak formats the game's engine can't mount. The
    /// property that matters is "4.x and intact", which is exactly what this reports.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task VerifyUnrealPakAsync()
    {
        if (string.IsNullOrWhiteSpace(UnrealPakExePath))
        {
            UnrealPakStatusMessage = "No UnrealPak.exe set — install it, or Browse… to one you already have.";
            return;
        }

        UnrealPakStatusMessage = "Checking…";
        var result = await _unrealPakInstaller.VerifyAsync(UnrealPakExePath);
        UnrealPakStatusMessage = result.Health switch
        {
            UnrealPakHealth.Ok when result.EngineVersion is not null =>
                $"Working — UnrealPak {result.EngineVersion}"
                + (result.EngineVersion.StartsWith('4') ? " (UE4, matches Icarus)." : " — WARNING: not a UE4 build; its paks may not load in Icarus."),
            UnrealPakHealth.Ok => "Working.",
            UnrealPakHealth.Missing => $"Not found at that path. {result.Detail}",
            _ => $"Broken copy: {result.Detail} Reinstall fixes this.",
        };
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task InstallBundledUnrealPakAsync()
    {
        try
        {
            UnrealPakStatusMessage = "Installing…";
            var exePath = await _unrealPakInstaller.InstallAsync();
            UnrealPakExePath = exePath;
            Save();
            await VerifyUnrealPakAsync();
        }
        catch (Exception ex)
        {
            UnrealPakStatusMessage = $"Install failed: {ex.Message}";
        }
    }

    // --- First-launch setup ---

    /// <summary>
    /// Set synchronously, at the start of the launch flow, whenever any prerequisite turned out
    /// to be missing. App.xaml.cs reads it right after constructing this VM to land that launch
    /// on the Settings page, so what the steps below did (found the game via Steam, installed
    /// UnrealPak, is extracting game data — all reported through this page's own status lines)
    /// is actually visible instead of happening behind the Library page. Deliberately not
    /// navigated from in here: this runs inside the constructor's own fire-and-forget call, and
    /// a NavigateToPageMessage handled mid-construction would resolve this same singleton
    /// re-entrantly.
    /// </summary>
    public bool FirstLaunchSetupNeededAttention { get; private set; }

    /// <summary>The constructor's launch flow, exposed so tests can await it instead of racing its fire-and-forget start.</summary>
    internal Task FirstLaunchSetupTask { get; }

    /// <summary>
    /// Every prerequisite in one place, in dependency order, so a fresh install is usable without
    /// a trip through Settings — a real complaint ("shouldn't all prerequisites be done at first
    /// launch?"): the game folder was never asked for at all, so the Launch game / UE4SS folder /
    /// Game folder quick-links sat greyed out with no hint why, and nothing ever offered the first
    /// data extraction either. Each step is quiet when already satisfied, so a configured install
    /// sees nothing.
    ///   1. Icarus Content folder — auto-detected through Steam and saved silently when found,
    ///      else a Browse offer. Re-checked every launch while still empty, same as UnrealPak.
    ///   2. UnrealPak.exe — the existing adopt / install / Browse offer.
    ///   3. The first "Update data folder" extraction — offered ONCE (FirstLaunchSetupCompletedAtUtc),
    ///      and only once 1 and 2 are both set, since it needs both. Declining just leaves the
    ///      button in Settings, where it always was.
    /// </summary>
    private async Task RunFirstLaunchSetupAsync()
    {
        try
        {
            EnsureIcarusContentFolderOnLaunch();
            await EnsureUnrealPakOnLaunchAsync();
            await OfferFirstDataExtractionAsync();
        }
        catch (Exception ex)
        {
            // Never let setup take the launch down — everything here can still be done by hand.
            _activityLog.Log($"First-launch setup hit an error ({ex.Message}) — finish setting up in Settings.", ActivityEntryKind.Warning);
        }
    }

    private void EnsureIcarusContentFolderOnLaunch()
    {
        if (!string.IsNullOrWhiteSpace(IcarusContentPath))
        {
            return;
        }

        FirstLaunchSetupNeededAttention = true;

        // The same Steam lookup the Auto-detect button runs — but saved on the spot rather than
        // pre-filled for a Save click, since there's nothing for the user to decide when the real
        // install was found.
        var detected = _steamInstallLocator.FindIcarusContentPath();
        if (detected is not null)
        {
            IcarusContentPath = detected;
            Save();
            SavedMessage = $"Found your Icarus install through Steam and saved it: {detected}";
            return;
        }

        var browse = _dialogService.Confirm(
            "IcarusStarlink needs to know where Icarus is installed, and couldn't find it through Steam.\n\n"
            + "Browse to the game's Content folder now? (It's Icarus\\Icarus\\Content inside the game install. Choose No to set it later in Settings — launching the game, the folder shortcuts, merging and installing all need it.)",
            "Set up your Icarus folder", ThemedConfirmSeverity.Question);
        if (browse)
        {
            BrowseIcarusContentFolder();
            if (!string.IsNullOrWhiteSpace(IcarusContentPath))
            {
                Save();
                return;
            }
        }

        SavedMessage = "Icarus folder not set yet — use Auto-detect or Browse… above, then Save settings.";
    }

    private async Task OfferFirstDataExtractionAsync()
    {
        if (_settingsService.Current.FirstLaunchSetupCompletedAtUtc is not null)
        {
            return;
        }

        // Not ready to offer yet (either prerequisite still missing, after the steps above had
        // their say) — leave it unmarked so the NEXT launch that does have both gets the offer.
        if (string.IsNullOrWhiteSpace(IcarusContentPath) || string.IsNullOrWhiteSpace(UnrealPakExePath) || !File.Exists(UnrealPakExePath))
        {
            return;
        }

        // An install that predates this flow and already extracted its data has nothing to be
        // offered — just record that setup is done so this check stops running.
        if (_settingsService.Current.LastDataFolderUpdatedAt is null)
        {
            FirstLaunchSetupNeededAttention = true;
            var extract = _dialogService.Confirm(
                "One last step: extract the game's data now?\n\n"
                + "This pulls the current mod-relevant data out of Icarus (takes a minute or so) — merging, installing, editing and checking mods all work against it. "
                + "Choose No to do it later with Refresh game data in Settings; re-run that whenever the game updates.",
                "Extract game data", ThemedConfirmSeverity.Question);
            if (extract)
            {
                await UpdateDataFolderAsync();
            }
            else
            {
                DataFolderStatusMessage = "Not extracted yet — click Refresh game data whenever you're ready (and after each game update).";
            }
        }

        // Same one-time moment: every Nexus website download depends on this app being the nxm://
        // handler, and the switch was buried in Settings → Nexus (a real complaint about setup).
        if (!_nxmProtocolRegistrar.IsRegisteredToThisApp())
        {
            FirstLaunchSetupNeededAttention = true;
            var register = _dialogService.Confirm(
                "Make IcarusStarlink handle Nexus \"Mod Manager Download\" links?\n\n"
                + "This registers the app as your nxm:// handler (a per-user Windows setting, no admin needed) so clicking Download on a Nexus mod page sends the file straight here. "
                + "You can change it any time in Settings → Nexus.",
                "Nexus download links", ThemedConfirmSeverity.Question);
            if (register)
            {
                // The registrar directly — the Settings-page command above this one asks its own
                // confirmation, and this offer already was the confirmation.
                try
                {
                    _nxmProtocolRegistrar.Register();
                    IsNxmProtocolRegisteredToThisApp = _nxmProtocolRegistrar.IsRegisteredToThisApp();
                    NxmProtocolStatusMessage = "Registered — Nexus \"Mod Manager Download\" buttons now open IcarusStarlink.";
                }
                catch (Exception ex)
                {
                    NxmProtocolStatusMessage = $"Registration failed: {ex.Message} — try again from Settings → Nexus.";
                }
            }
        }

        _settingsService.Current.FirstLaunchSetupCompletedAtUtc = DateTimeOffset.UtcNow;
        _settingsService.Save();
    }

    /// <summary>
    /// First-launch (or any launch with no working UnrealPak): silently adopt a copy that's
    /// already installed next to the app, else offer to install the bundled payload, else point
    /// the user at Browse — the "verify, locate, or install next to the exe" flow. Step 2 of
    /// RunFirstLaunchSetupAsync; quiet whenever the configured path simply works.
    /// </summary>
    private async Task EnsureUnrealPakOnLaunchAsync()
    {
        if (!string.IsNullOrWhiteSpace(UnrealPakExePath) && File.Exists(UnrealPakExePath))
        {
            return;
        }

        FirstLaunchSetupNeededAttention = true;

        // A release zip whose Tools folder was pre-extracted (or a previous install) — adopt it
        // without ceremony; there's nothing to ask.
        if (File.Exists(_unrealPakInstaller.InstalledExePath))
        {
            UnrealPakExePath = _unrealPakInstaller.InstalledExePath;
            Save();
            UnrealPakStatusMessage = "Using the copy installed next to the app.";
            return;
        }

        var answer = _dialogService.Confirm(
            "IcarusStarlink needs UnrealPak.exe to build and unpack mod paks, and none is set up yet.\n\n"
            + "Install it now? (Downloads a small, one-time payload if there isn't already a copy shipped alongside the app. Choose No to point at one you already have, in Settings.)",
            "Set up UnrealPak", ThemedConfirmSeverity.Question);
        if (answer)
        {
            await InstallBundledUnrealPakAsync();
        }
        else
        {
            UnrealPakStatusMessage = "No UnrealPak.exe set — install it, or Browse… to one you already have.";
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
        // UE4SS follows the saved game folder: this is when a newly chosen install's UE4SS shows.
        RefreshUe4ssStatus();
        WeakReferenceMessenger.Default.Send(new SettingsSavedMessage());
    }

    /// <summary>Refresh game data, for Merge &amp; Install's "Refresh game data, then build" (a no-op while one is already running).</summary>
    /// <remarks>While one is running this returns THAT run (it used to return an already-finished
    /// task, so a caller checked "is the data fresh now?" while the extraction was still going).</remarks>
    /// <remarks>Not refused while other work runs, unlike the button: its callers ARE that work
    /// (Merge &amp; Install's refresh-then-build, the game-update run).</remarks>
    public Task RefreshGameDataAsync() => IsUpdatingDataFolder ? _dataFolderUpdate : StartDataFolderUpdate();

    private bool CanUpdateDataFolder() => !IsUpdatingDataFolder;

    /// <summary>The Refresh game data button (Settings and Weekly Changes).</summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true, CanExecute = nameof(CanUpdateDataFolder))]
    private Task UpdateDataFolderAsync()
    {
        // A merge being built reads the Data folder this rewrites, and a download being imported
        // checks against it: refreshing under them mixed two game versions (review 2026-10-23).
        if (!IsUpdatingDataFolder && AppBusyCheck.DescribeRunning() is { } running)
        {
            DataFolderStatusMessage = $"Wait until {running} has finished, then click Refresh game data again.";
            return Task.CompletedTask;
        }

        return StartDataFolderUpdate();
    }

    private Task StartDataFolderUpdate()
    {
        // Two extractions into the same Data folder at once (the button, first-launch setup and
        // the game-update run can all start one) would overwrite each other's files.
        if (IsUpdatingDataFolder)
        {
            return _dataFolderUpdate;
        }

        _dataFolderUpdate = UpdateDataFolderCoreAsync();
        return _dataFolderUpdate;
    }

    private async Task UpdateDataFolderCoreAsync()
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
        HasWeeklyChanges = false;

        try
        {
            var previousUpdateAt = _settingsService.Current.LastDataFolderUpdatedAt;
            var result = await _unrealPakService.ExtractDataPakAsync(UnrealPakExePath, IcarusContentPath, _dataOutputDirectory, previousUpdateAt);

            if (result.ChangeReport is { } report)
            {
                _weeklyChangeReportStore.Save(report);
            }

            // This game version joins the row history, so rows a later update removes are known as
            // the game's (an old copy a mod carries), not a mod's new content.
            try
            {
                var dataFolder = _dataOutputDirectory;
                await Task.Run(() => GameRowHistory.Record(dataFolder, GameRowHistory.LocalFileFor(dataFolder), DateTimeOffset.UtcNow));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _activityLog.Log($"Couldn't add this game version to the row history: {ex.Message}", ActivityEntryKind.Warning);
            }

            // Recorded regardless of whether a report was produced (also covers the first-ever
            // run, which has nothing to diff against yet) — this is what the *next* run's
            // previousUpdateAt, and CheckForGameUpdateAsync's own baseline, come from.
            _settingsService.Current.LastDataFolderUpdatedAt = DateTimeOffset.UtcNow;
            _settingsService.Current.LastDataPakHash = await _unrealPakService.TryGetDataPakHashAsync(IcarusContentPath);
            _settingsService.Save();
            GameDataOutdatedMessage = null;
            OnPropertyChanged(nameof(DataFolderSummary));
            WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

            DataFolderStatusMessage = $"Extracted {result.ExtractedFileCount} files. {DescribeChangeReport(result.ChangeReport)}";
            HasWeeklyChanges = result.ChangeReport is { ChangedFiles.Count: > 0 };
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else in this app: a wrong path, a UnrealPak.exe that
            // can't run, or the game having moved/renamed data.pak should show a status message,
            // not crash the app. A raw exception message alone gives a new user nothing to act on
            // when the real cause is UnrealPak.exe itself — a missing sibling DLL or antivirus
            // quarantine makes Process.Start throw a cryptic Win32 error, and a corrupted extract
            // of the bundled payload can fail with an equally unhelpful non-zero exit. Re-running
            // the same health check "Check" already offers distinguishes that case and reuses its
            // exact, actionable wording (pointing at Reinstall) instead of duplicating it here.
            var verify = await _unrealPakInstaller.VerifyAsync(UnrealPakExePath);
            DataFolderStatusMessage = verify.Health switch
            {
                UnrealPakHealth.Ok => $"Refresh failed: {ex.Message}",
                UnrealPakHealth.Missing => $"Refresh failed: {(verify.Detail ?? "UnrealPak.exe isn't at that path").TrimEnd('.')}. "
                    + "Reinstall in the UnrealPak section above.",
                _ => $"Refresh failed: your UnrealPak.exe copy looks broken ({verify.Detail?.TrimEnd('.')}). "
                    + "Reinstall in the UnrealPak section above fixes this.",
            };
        }
        finally
        {
            IsUpdatingDataFolder = false;
        }
    }

    [RelayCommand]
    private void ViewWeeklyChanges() => WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("weekly-changes"));

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
        if (await _gameDataFreshness.IsGameDataOutdatedAsync())
        {
            GameDataOutdatedMessage = "Icarus has been updated since your last data refresh — click Refresh game data to see what changed.";
        }
    }

    partial void OnIsAuthorizingNexusChanged(bool value) => OnPropertyChanged(nameof(CanAuthorizeNexus));

    [RelayCommand]
    private void OpenNexusApiKeyPage() => UrlOpener.TryOpen("https://www.nexusmods.com/users/myaccount?tab=api");

    /// <summary>
    /// A real, live validation against Nexus's own API — "just like a new user would," per the
    /// user's own explicit ask, not a hardcoded test key. Only saves the key to Windows Credential
    /// Manager once Nexus itself has confirmed it's genuinely valid.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
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
            WeakReferenceMessenger.Default.Send(new NexusAccountChangedMessage());
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
        WeakReferenceMessenger.Default.Send(new NexusAccountChangedMessage());
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
        var result = _dialogService.Confirm(
            "This registers IcarusStarlink as the handler for nxm:// links (Nexus's \"Mod Manager Download\" buttons) for your Windows account.\n\n" +
            "If another mod manager (e.g. Vortex) currently handles these, Starlink takes over but passes it the links for its own games, so it keeps working. Unregister below gives the links back to it.\n\n" +
            "Continue?",
            "Register as Nexus download handler", ThemedConfirmSeverity.Question);

        if (!(result))
        {
            return;
        }

        try
        {
            _nxmProtocolRegistrar.Register();
            RefreshNxmHandlerState();
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
            // Turned off on purpose: no "another app took them" warnings or automatic take-backs.
            _settingsService.Current.NxmHandlerSetUp = false;
            _settingsService.Save();
            RefreshNxmHandlerState();
            NxmProtocolStatusMessage = NxmHandlerOwner is { } owner
                ? $"Unregistered — Nexus download links open {owner} again."
                : "Unregistered.";
        }
        catch (Exception ex)
        {
            NxmProtocolStatusMessage = $"Couldn't unregister: {ex.Message}";
        }
    }

    /// <summary>
    /// The game folder UE4SS is installed into, checked and removed from: the SAVED one. Browse and
    /// Auto-detect only fill the box until Save settings (the page's model), but UE4SS used the box,
    /// so it could install into a folder the rest of the app wasn't using (review 2026-10-23).
    /// </summary>
    private string? Ue4ssContentPath => string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath) ? null : _settingsService.Current.IcarusContentPath;

    private const string Ue4ssNeedsGameFolder = "Set the Icarus Content folder and click Save settings first.";

    /// <summary>
    /// A UE4SS refusal or failure: on this page's status line and in the activity log (a pop-up),
    /// since the Library's "Install UE4SS" bar runs these commands with Settings not showing, and
    /// used to give no answer at all (review 2026-10-23).
    /// </summary>
    private void ReportUe4ssProblem(string status, string? logged = null)
    {
        Ue4ssStatusMessage = status;
        _activityLog.Log(logged ?? status, ActivityEntryKind.Warning);
    }

    private void RefreshUe4ssStatus()
    {
        if (Ue4ssContentPath is not { } contentPath)
        {
            IsUe4ssInstalled = false;
            Ue4ssInstalledVersion = null;
            Ue4ssInstalledCommit = null;
            return;
        }

        try
        {
            var status = _ue4ssLoaderInstallService.GetStatus(contentPath);
            IsUe4ssInstalled = status.IsInstalled;
            Ue4ssInstalledVersion = status.InstalledVersion;
            Ue4ssInstalledCommit = status.InstalledCommit;
        }
        catch (Exception)
        {
            // A path with no parent (a drive root picked in Browse) makes the game-path resolver
            // throw; from inside a property-changed hook that took the whole app down (an audit
            // finding). Not a real install = not installed.
            IsUe4ssInstalled = false;
            Ue4ssInstalledVersion = null;
            Ue4ssInstalledCommit = null;
        }
    }


    /// <summary>
    /// Only when the user asks (the UE4SS tab's button, or the app's own Check for updates):
    /// fetches GitHub's newest UE4SS dev build and works out whether it is newer than the
    /// installed one. Never installs anything.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CheckForUe4ssUpdateAsync()
    {
        if (IsCheckingUe4ssRelease)
        {
            return;
        }

        IsCheckingUe4ssRelease = true;
        try
        {
            RefreshUe4ssStatus();

            var latest = await _ue4ssReleaseClient.GetLatestDevBuildAsync();
            if (latest is null)
            {
                Ue4ssStatusMessage = GitHubApiCache.RateLimitMessage is { } limit
                    ? $"Couldn't check for a newer UE4SS. {limit}"
                    : "Couldn't check for a newer UE4SS: GitHub didn't answer (offline, or GitHub is down).";
                return;
            }

            Ue4ssLatestRelease = latest;
            Ue4ssCheckedOrder = Ue4ssInstalledCommit is { } installed
                ? await _ue4ssReleaseClient.CompareBuildsAsync(installed, Ue4ssInstallCommit)
                : Ue4ssBuildOrder.Unknown;
            HasCheckedUe4ssRelease = true;

            var newest = Ue4ssBuild.Describe(latest.Version, latest.Commit, latest.BuildNumber);
            Ue4ssStatusMessage = !IsUe4ssInstalled ? $"The newest UE4SS is {newest}."
                : Ue4ssInstallOrder switch
                {
                    Ue4ssBuildOrder.Same => "You have the newest UE4SS dev build.",
                    Ue4ssBuildOrder.Newer => $"UE4SS {newest} is available.",
                    Ue4ssBuildOrder.Older => $"Your UE4SS is newer than the newest dev build on GitHub ({newest}).",
                    _ => $"The newest UE4SS is {newest}. Starlink couldn't tell how yours compares.",
                };
        }
        finally
        {
            IsCheckingUe4ssRelease = false;
        }
    }

    /// <summary>The paragraph about the user's C++ mods for an install/update confirmation; empty when there are none or the build isn't changing.</summary>
    private string NativeModsWarning() => NativeModsWarningFor(changing: Ue4ssInstallOrder != Ue4ssBuildOrder.Same);

    private string NativeModsWarningFor(bool changing)
    {
        if (!changing || !IsUe4ssInstalled || Ue4ssContentPath is not { } contentPath)
        {
            return "";
        }

        IReadOnlyList<string> native;
        try
        {
            native = _ue4ssLoaderInstallService.ListNativeMods(contentPath);
        }
        catch (Exception)
        {
            return "";
        }

        return native.Count == 0 ? ""
            : $"These mods are C++ mods: {string.Join(", ", native)}. A C++ mod is made for one UE4SS build, so after this change it may stop loading " +
              "until it's rebuilt for the new build. Lua mods aren't affected. If something breaks, the UE4SS you have now is in Backups\\UE4SS-Loader.\n\n";
    }

    /// <summary>
    /// Phase 8.5: a real, hard-to-reverse write into Binaries\Win64 — the game's own executable
    /// folder, more sensitive than either target this app has written to before (Content\Paks\mods,
    /// the UE4SS Mods folder). Same gating philosophy as the real pak install and the nxm://
    /// protocol registration: never automatic, and only proceeds behind this explicit confirmation.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task InstallOrUpdateUe4ssAsync()
    {
        // The Library's "install UE4SS" bar calls this command directly, so the button's own
        // IsEnabled isn't the only way in: never two installs (or install + uninstall) at once.
        if (IsInstallingUe4ss || IsUninstallingUe4ss)
        {
            return;
        }

        if (Ue4ssContentPath is not { } contentPath)
        {
            ReportUe4ssProblem(Ue4ssNeedsGameFolder, $"Couldn't install UE4SS: {Ue4ssNeedsGameFolder} (Settings › Game & tools)");
            return;
        }

        var fromGitHub = UseGitHubUe4ss;
        var version = Ue4ssInstallVersion;
        var commit = Ue4ssInstallCommit;
        var label = Ue4ssInstallLabel;
        var wasInstalled = IsUe4ssInstalled;
        var order = Ue4ssInstallOrder;
        var verb = !wasInstalled ? "Install" : order == Ue4ssBuildOrder.Newer ? "Update" : "Reinstall";
        var current = wasInstalled && Ue4ssInstalledVersion is not null ? $"You have UE4SS {Ue4ssInstalledLabel}. " : "";
        var downgrade = order == Ue4ssBuildOrder.Older
            ? "Yours is newer: this replaces it with an older build. To keep yours, cancel.\n\n"
            : "";
        var result = _dialogService.Confirm(
            current + downgrade + (fromGitHub
                ? $"This downloads UE4SS {label} from GitHub and installs it into your game's Binaries\\Win64 folder "
                : $"This installs UE4SS {label}, which comes with Starlink (no download), into your game's Binaries\\Win64 folder ") +
            "— the loader that lets Lua/scripting mods run.\n\n" +
            NativeModsWarning() +
            "Your existing UE4SS.dll/dwmapi.dll are backed up first (last 5 kept). Any mods already in your Mods folder, " +
            "and your own UE4SS-settings.ini if you've customized it, are left untouched.\n\n" +
            "Continue?",
            $"{verb} UE4SS", ThemedConfirmSeverity.Question);

        if (!(result))
        {
            return;
        }

        if (IsGameRunning())
        {
            ReportUe4ssProblem("Icarus is running — close it before installing UE4SS, or the game may lock the files being replaced.");
            return;
        }

        IsInstallingUe4ss = true;
        Ue4ssStatusMessage = fromGitHub ? "Downloading…" : "Preparing…";
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"UE4SS_{Guid.NewGuid():N}.zip");
        try
        {
            if (fromGitHub)
            {
                var release = Ue4ssLatestRelease!;
                var bytes = await _httpClient.GetByteArrayAsync(release.DownloadUrl);
                await File.WriteAllBytesAsync(tempZipPath, bytes);

                // A real bug found live: unlike this app's own self-update (AppUpdateClient), this
                // download had no integrity check at all — it writes a third-party DLL loader straight
                // into the game's own Binaries\Win64 folder. Same GitHubAssetIntegrity check, same
                // "null digest skips rather than fails" tolerance — see its own doc comment.
                await GitHubAssetIntegrity.VerifySha256Async(release.Digest, tempZipPath);
            }
            else
            {
                // The bundled copy checks its own hash on the way out.
                tempZipPath = await BundledUe4ss.ExtractToTempAsync();
            }

            // A SECOND, LATE check, immediately before the real write below — the first
            // IsGameRunning() check above (before the confirm dialog and the download itself) can go
            // stale for however long those take, and Icarus could be launched in that window. This
            // doesn't close the race entirely — it only shrinks the exposed window down to
            // "immediately before the write" instead of "however long the dialog+download took".
            if (IsGameRunning())
            {
                ReportUe4ssProblem("Icarus started running while this install was about to write — nothing was touched. Close it and try again.",
                    "UE4SS wasn't installed: Icarus started running just before. Close it and try again.");
                return;
            }

            Ue4ssStatusMessage = "Installing…";
            await _ue4ssLoaderInstallService.InstallOrUpdateAsync(contentPath, tempZipPath, _backupDirectory, version, commit);

            RefreshUe4ssStatus();
            Ue4ssCheckedOrder = Ue4ssBuildOrder.Unknown;
            // From the same verb the confirm used: a reinstall or an older build isn't an "update".
            Ue4ssStatusMessage = verb switch
            {
                "Install" => $"Installed UE4SS {label}.",
                "Update" => $"Updated to UE4SS {label}.",
                _ => $"Reinstalled UE4SS {label}.",
            };
            WeakReferenceMessenger.Default.Send(new Ue4ssLoaderChangedMessage($"UE4SS {label} is installed in the game."));
        }
        catch (Exception ex)
        {
            ReportUe4ssProblem($"{verb} failed: {ex.Message}", $"UE4SS {verb.ToLowerInvariant()} failed: {ex.Message}");
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


    /// <summary>
    /// Installs UE4SS from a zip the user downloaded (a version GitHub no longer lists as latest,
    /// or a PC offline). Same safety as Install: confirmation, game-running checks, backup first,
    /// mods and a customised UE4SS-settings.ini kept. The installer refuses a zip without
    /// UE4SS.dll and dwmapi.dll at its root before writing anything.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task InstallUe4ssFromZipAsync()
    {
        if (Ue4ssContentPath is not { } contentPath)
        {
            ReportUe4ssProblem(Ue4ssNeedsGameFolder, $"Couldn't install UE4SS: {Ue4ssNeedsGameFolder} (Settings › Game & tools)");
            return;
        }

        if (IsInstallingUe4ss || IsUninstallingUe4ss
            || _dialogService.PickFile("Pick a UE4SS release zip (for example UE4SS_v3.0.1-1181-g83330635.zip)", "UE4SS release (*.zip)|*.zip") is not { } zipPath)
        {
            return;
        }

        // A release zip keeps GitHub's name ("UE4SS_v3.0.1-1176-g1ba38db4.zip"), which says which build it is.
        var knownBuild = Ue4ssBuild.TryParseDevAssetName(Path.GetFileName(zipPath), out var zipVersion, out _, out var zipCommit);
        var changesBuild = !knownBuild || !Ue4ssBuild.SameCommit(zipCommit, Ue4ssInstalledCommit);
        if (!_dialogService.Confirm(
                $"Install UE4SS from\n{zipPath}\ninto your game's Binaries\\Win64 folder?\n\n" +
                (changesBuild ? NativeModsWarningFor(changing: true) : "") +
                "Your existing UE4SS.dll/dwmapi.dll are backed up first (last 5 kept). Mods already in your Mods folder, " +
                "and your own UE4SS-settings.ini, are left untouched.",
                "Install UE4SS from a zip", ThemedConfirmSeverity.Question))
        {
            return;
        }

        if (IsGameRunning())
        {
            ReportUe4ssProblem("Icarus is running — close it before installing UE4SS, or the game may lock the files being replaced.");
            return;
        }

        IsInstallingUe4ss = true;
        Ue4ssStatusMessage = "Installing…";
        try
        {
            await _ue4ssLoaderInstallService.InstallOrUpdateAsync(
                contentPath, zipPath, _backupDirectory, knownBuild ? zipVersion : null, knownBuild ? zipCommit : null);
            RefreshUe4ssStatus();
            Ue4ssCheckedOrder = Ue4ssBuildOrder.Unknown;
            Ue4ssStatusMessage = $"Installed UE4SS{(Ue4ssInstalledVersion is not null ? $" {Ue4ssInstalledLabel}" : "")} from {Path.GetFileName(zipPath)}.";
            WeakReferenceMessenger.Default.Send(new Ue4ssLoaderChangedMessage(Ue4ssStatusMessage));
        }
        catch (Exception ex)
        {
            ReportUe4ssProblem($"Install failed: {ex.Message}", $"UE4SS install failed: {ex.Message}");
        }
        finally
        {
            IsInstallingUe4ss = false;
        }
    }

    /// <summary>
    /// The last item of the big plan, deliberately saved for last: removes the UE4SS loader
    /// entirely (dwmapi.dll + the ue4ss folder). The safety story, in order: the user's own mods —
    /// everything NOT in UE4SS's own mods.json manifest — are moved to this app's staging first,
    /// the whole install is backed up (keep-last-5), and only then does anything get deleted.
    /// Icarus's own files are never touched; UE4SS lives entirely in those two paths.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task UninstallUe4ssAsync()
    {
        if (Ue4ssContentPath is not { } contentPath)
        {
            Ue4ssStatusMessage = Ue4ssNeedsGameFolder;
            return;
        }

        if (!IsUe4ssInstalled)
        {
            Ue4ssStatusMessage = "UE4SS isn't installed — nothing to remove.";
            return;
        }

        if (IsInstallingUe4ss || IsUninstallingUe4ss)
        {
            return;
        }

        // The user's own mods are named in the warning itself, so what's about to be preserved is
        // visible BEFORE anything happens, not discovered from a report afterwards.
        IReadOnlyList<string> userMods;
        try
        {
            userMods = _ue4ssLoaderInstallService.ListUserAddedMods(contentPath);
        }
        catch (Exception ex)
        {
            // Outside the try below, a failure here ended up only in the crash log with nothing on screen.
            Ue4ssStatusMessage = $"Couldn't read UE4SS's Mods folder: {ex.Message}";
            return;
        }

        var userModsLine = userMods.Count > 0
            ? $"Your own {userMods.Count} mod(s) — {string.Join(", ", userMods)} — are moved back to this app's staging first (re-enable them if you ever reinstall UE4SS).\n\n"
            : "No mods of your own were found in its Mods folder.\n\n";

        var result = _dialogService.Confirm(
            "This removes the UE4SS loader completely from your game's Binaries\\Win64 folder (dwmapi.dll and the ue4ss folder) — "
            + "Lua/scripting mods stop working until it's reinstalled.\n\n"
            + userModsLine
            + "Everything removed is backed up first (last 5 kept), and Icarus's own files are not touched.\n\nContinue?",
            "Uninstall UE4SS", ThemedConfirmSeverity.Warning);
        if (!(result))
        {
            return;
        }

        if (IsGameRunning())
        {
            Ue4ssStatusMessage = "Icarus is running — close it before uninstalling UE4SS, or the game may lock the files being removed.";
            return;
        }

        IsUninstallingUe4ss = true;
        try
        {
            // A SECOND, LATE check, immediately before the real removal below — the first
            // IsGameRunning() check above (before the confirm dialog) can go stale for however long
            // the user takes to answer that dialog, and Icarus could be launched in that window. This
            // doesn't close the race entirely — it only shrinks the exposed window down to
            // "immediately before the write" instead of "however long the confirm dialog was up".
            if (IsGameRunning())
            {
                Ue4ssStatusMessage = "Icarus started running while this uninstall was about to write — nothing was touched. Close it and try again.";
                return;
            }

            Ue4ssStatusMessage = "Uninstalling…";
            var uninstallResult = await _ue4ssLoaderInstallService.UninstallAsync(contentPath, _stagedUe4ssDirectory, _backupDirectory);

            RefreshUe4ssStatus();
            Ue4ssStatusMessage = uninstallResult.PreservedUserMods.Count > 0
                ? $"UE4SS removed. Your {uninstallResult.PreservedUserMods.Count} mod(s) were kept in this app's staging; the rest is backed up."
                : "UE4SS removed. A backup was kept.";
            WeakReferenceMessenger.Default.Send(new Ue4ssLoaderChangedMessage(Ue4ssStatusMessage));
        }
        catch (Exception ex)
        {
            ReportUe4ssProblem($"Uninstall failed: {ex.Message}", $"UE4SS uninstall failed: {ex.Message}");
        }
        finally
        {
            IsUninstallingUe4ss = false;
        }
    }

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

    /// <summary>Settings › Appearance › Show pop-up messages. Saved straight away like Performance tracking; turning it off also clears any cards showing.</summary>
    [ObservableProperty]
    private bool _showPopUpMessages;

    partial void OnShowPopUpMessagesChanged(bool value) =>
        _settingsService.SetAndSave(s => s.ShowPopUpMessages, (s, v) => s.ShowPopUpMessages = v, value);

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

    /// <summary>The release this channel offers (AppUpdateChoice.Pick); betas are only looked up on the Beta channel.</summary>
    private async Task<AppUpdateRelease?> GetChannelReleaseAsync()
    {
        var betaChannel = UpdateChannel == BetaChannel;
        var stable = await _appUpdateClient.GetLatestReleaseAsync();
        var beta = betaChannel ? await _appUpdateClient.GetLatestBetaReleaseAsync() : null;
        return AppUpdateChoice.Pick(stable, beta, betaChannel);
    }

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

        IsInstallingAppUpdate = true;
        try
        {
            // Earlier updates' work folders (zip + full extracted build, ~2x the release size each)
            // were never cleaned up — by anyone, since the updater runs from one and the app never
            // looked (an audit finding). Any previous AppUpdate_* folder is finished with by now.
            var updateWorkRoot = Path.Combine(Path.GetTempPath(), "IcarusStarlink");
            if (Directory.Exists(updateWorkRoot))
            {
                foreach (var stale in Directory.GetDirectories(updateWorkRoot, "AppUpdate_*"))
                {
                    try
                    {
                        Directory.Delete(stale, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Best-effort; retried on the next update.
                    }
                }
            }

            var workDirectory = Path.Combine(updateWorkRoot, $"AppUpdate_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDirectory);

            AppUpdateStatusMessage = $"Downloading v{release.Version}…";
            var zipPath = Path.Combine(workDirectory, "update.zip");
            await _appUpdateClient.DownloadAssetAsync(release, zipPath);

            AppUpdateStatusMessage = "Preparing update…";
            var extractDirectory = Path.Combine(workDirectory, "new");
            await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDirectory));

            // Tolerate a release zip that wraps everything in a single top-level folder — a common
            // way zips get built — as long as the app exe is findable one level down.
            var newFilesRoot = extractDirectory;
            if (!File.Exists(Path.Combine(newFilesRoot, "IcarusStarlink.App.exe")))
            {
                var subdirectories = Directory.GetDirectories(newFilesRoot);
                if (subdirectories.Length == 1 && File.Exists(Path.Combine(subdirectories[0], "IcarusStarlink.App.exe")))
                {
                    newFilesRoot = subdirectories[0];
                }
                else
                {
                    AppUpdateStatusMessage = "The downloaded release zip doesn't contain IcarusStarlink.App.exe — not installing it.";
                    return;
                }
            }

            // The updater must run from OUTSIDE the install folder or it couldn't replace its own
            // files: stage it into the temp work folder and launch from there. In a published
            // release the Updater is a single self-contained exe (see IcarusStarlink.App.csproj's
            // PublishSingleFileUpdater target for the real bug that forced that: staging the old
            // four-file self-contained layout without its runtime couldn't even start), so only
            // the .exe is required. A plain dev build still has the framework-dependent four-file
            // shape, so its three companions are staged too whenever they happen to be present.
            var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var updaterStaging = Path.Combine(workDirectory, "updater");
            Directory.CreateDirectory(updaterStaging);
            var updaterExeSource = Path.Combine(installDirectory, "IcarusStarlink.Updater.exe");
            if (!File.Exists(updaterExeSource))
            {
                AppUpdateStatusMessage = "Couldn't stage the updater — IcarusStarlink.Updater.exe is missing next to the app.";
                return;
            }

            File.Copy(updaterExeSource, Path.Combine(updaterStaging, "IcarusStarlink.Updater.exe"));
            foreach (var companion in new[] { "IcarusStarlink.Updater.dll", "IcarusStarlink.Updater.runtimeconfig.json", "IcarusStarlink.Updater.deps.json" })
            {
                var source = Path.Combine(installDirectory, companion);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(updaterStaging, companion));
                }
            }

            // Looked at again: the download took a while, and server work may have started meanwhile.
            if (DescribeBackgroundWork() is { } startedMeanwhile)
            {
                AppUpdateStatusMessage = startedMeanwhile;
                return;
            }

            var appExePath = Path.Combine(installDirectory, "IcarusStarlink.App.exe");
            Process.Start(new ProcessStartInfo(Path.Combine(updaterStaging, "IcarusStarlink.Updater.exe"))
            {
                ArgumentList = { Environment.ProcessId.ToString(), installDirectory, newFilesRoot, appExePath },
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            Application.Current.Shutdown();
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
                var whatsNew = new WhatsNewWindow(release.Version, release.ReleaseNotes) { Owner = Application.Current.MainWindow };
                whatsNew.ShowDialog();
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

    /// <summary>
    /// Silent once-per-launch check, same shape as CheckForAppUpdatesOnLaunchAsync — the closest
    /// thing this app can offer to "did that merge actually work in Icarus" without guessing at
    /// DataTable-parse-error text (see GameSessionHealthCheck's own doc comment for why). Only a
    /// real crash reported AFTER the last recorded Install logs anything; nothing here ever nags on
    /// a launch with no prior install, or one where the game simply never crashed.
    /// </summary>
    private void CheckForGameCrashesSinceLastInstall()
    {
        if (_settingsService.Current.LastInstallAtUtc is not { } lastInstallAtUtc)
        {
            return;
        }

        try
        {
            var crashes = GameSessionHealthCheck.FindCrashesSince(lastInstallAtUtc);
            if (crashes.Count == 0)
            {
                return;
            }

            var mostRecent = crashes[0];
            _activityLog.Log(
                $"Icarus crashed since your last install ({mostRecent.OccurredAtUtc.ToLocalTime():g}): {mostRecent.ErrorMessage}",
                ActivityEntryKind.Warning);

            // The other half of this same health check (see GameSessionHealthCheck's own doc
            // comment): a real crash is a strong enough signal to also show what UE4SS itself was
            // last doing, in case a UE4SS mod's load failure is what actually caused it — plain log
            // text, not pattern-matched, for the same "no guessing" reason FindCrashesSince's own
            // message isn't inferred either.
            if (!string.IsNullOrWhiteSpace(IcarusContentPath))
            {
                var logTail = GameSessionHealthCheck.ReadUe4ssLogTail(IcarusContentPath);
                if (logTail.Count > 0)
                {
                    _activityLog.Log(
                        $"UE4SS.log tail at the time of that crash:\n{string.Join('\n', logTail)}",
                        ActivityEntryKind.Info);
                }
            }
        }
        catch (Exception)
        {
            // Passive background check — a locked/inaccessible crash folder is not something to
            // ever surface as an error, same "can't tell, don't nag" contract every other silent
            // launch check in this class already follows.
        }
    }

    /// <summary>
    /// UE4SS install/uninstall write into the game's own Binaries\Win64 folder (dwmapi.dll,
    /// UE4SS.dll, the ue4ss folder) — files the running game process can hold open, the same class of
    /// risk SavesViewModel already guards with IGameProcessChecker. Checked once before the confirm
    /// dialog and again immediately before the real write, for the same reason: the first check can
    /// go stale for however long the user takes to answer that dialog (or, for install, the download
    /// on top of that).
    /// </summary>
    private bool IsGameRunning() => _gameProcessChecker.IsRunning();
}
