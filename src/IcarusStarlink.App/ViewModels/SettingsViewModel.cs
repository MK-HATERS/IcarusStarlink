using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Catalog.Ue4ss;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Skins;
using IcarusStarlink.Core.Steam;
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
    private string? _savedMessage;

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
}
