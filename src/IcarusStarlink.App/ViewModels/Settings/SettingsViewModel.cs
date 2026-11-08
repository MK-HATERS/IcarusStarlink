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
    private readonly string? _gameCrashesFolder;

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
        string stagedUe4ssDirectory, IGameDataFreshness? gameDataFreshness = null, string? gameCrashesFolder = null)
    {
        // Null: the game's own crash folder (%LocalAppData%\Icarus\Saved\Crashes).
        _gameCrashesFolder = gameCrashesFolder;
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
        // settings.json couldn't be used at start: the whole session runs on defaults, so say so
        // where it stays (the Activity panel) and on this page.
        if (_settingsService.LoadProblem is { } settingsProblem)
        {
            _activityLog.Log(settingsProblem, ActivityEntryKind.Warning);
            SavedMessage = settingsProblem;
        }

        RefreshNxmHandlerState();
        WeakReferenceMessenger.Default.Register<NxmHandlerChangedMessage>(this, (recipient, _) => ((SettingsViewModel)recipient).RefreshNxmHandlerState());
        WeakReferenceMessenger.Default.Register<StarlinkUpdateLinkMessage>(this, (recipient, message) =>
        {
            var offer = ((SettingsViewModel)recipient).OfferUpdateFromNexusLinkAsync(message.NxmUrl);
        });

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
        // Closing Starlink or updating it now would cut these off partway: the window close and
        // Update now ask every page what's running (AppBusyCheck).
        WeakReferenceMessenger.Default.Register<BackgroundWorkRequestMessage>(this, (recipient, message) =>
        {
            var settings = (SettingsViewModel)recipient;
            if (settings.IsMigratingFromImm)
            {
                message.Reply("a classic IMM migration");
            }

            if (settings.IsUpdatingDataFolder)
            {
                message.Reply("a game data refresh");
            }

            if (settings.IsInstallingUe4ss)
            {
                message.Reply("a UE4SS install");
            }

            if (settings.IsUninstallingUe4ss)
            {
                message.Reply("a UE4SS removal");
            }
        });
        // The first-launch questions and the "Update available" offer are NOT started here: they
        // show modal dialogs, and anything that resolved this singleton while one was open (opening
        // Settings, an nxm:// link, GameDataFreshness) re-entered the DI factory mid-construction
        // and built a second Settings that asked every question again.
        // App.xaml.cs starts them once this instance exists (StartFirstLaunchSetup).
        CheckForGameCrashesSinceLastInstall();
        ReportLastAppUpdateOutcome();
        _ = RefreshBackupsSizeAsync();

        var skin = _customSkinStore.Load(out var skinProblem);
        LoadSkinTokens(skin);
        if (skinProblem is not null)
        {
            SkinStatusMessage = $"Your skin file couldn't be used ({skinProblem}), so the boxes show the Icarus colours. "
                + "Fix the file and click Reload skin file. Save skin replaces it and keeps the old file beside it as a .bak.";
        }
    }

    [RelayCommand]
    private void Save()
    {
        // Any folder used to be saved as the Content folder. A game folder holding it is switched
        // to it; one without Data\data.pak is still saved (the game may be mid-install), with a warning.
        var switchedTo = SwitchToContentFolderInside();
        _settingsService.Current.IcarusContentPath = IcarusContentPath;
        _settingsService.Current.UnrealPakExePath = UnrealPakExePath;
        SavedMessage = !_settingsService.Save()
            ? "Couldn't save settings. settings.json may be open in another program (OneDrive, antivirus), or Starlink's folder may be read-only. "
                + "Close that program, then click Save settings again."
            : switchedTo is not null
            ? $"Saved at {DateTime.Now:T}, using {switchedTo} (the game's Content folder inside the one you gave)."
            : !string.IsNullOrWhiteSpace(IcarusContentPath) && !IcarusContentFolder.HasDataPak(IcarusContentPath)
            ? $"Saved at {DateTime.Now:T}, but that folder doesn't look like the game's Content folder (there's no Data\\data.pak in it). "
                + "Pick …\\Icarus\\Icarus\\Content: merging, installing and Refresh game data need it."
            : $"Saved at {DateTime.Now:T}";
        // UE4SS follows the saved game folder: this is when a newly chosen install's UE4SS shows.
        RefreshUe4ssStatus();
        WeakReferenceMessenger.Default.Send(new SettingsSavedMessage());
    }
}
