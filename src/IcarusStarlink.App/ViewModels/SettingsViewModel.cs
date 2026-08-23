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
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Catalog.Ue4ss;
using IcarusStarlink.Core.Migration;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Skins;
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
    private readonly IThemeService _themeService;
    private readonly ICustomSkinStore _customSkinStore;
    private readonly ImmMigrationService _immMigrationService;
    private readonly IUnrealPakInstaller _unrealPakInstaller;

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
        IThemeService themeService, ICustomSkinStore customSkinStore, ImmMigrationService immMigrationService,
        Func<MergeInstallViewModel> mergeInstallViewModel, IUnrealPakInstaller unrealPakInstaller,
        string backupDirectory, string dataOutputDirectory, string logsDirectory, string settingsFilePath)
    {
        _unrealPakInstaller = unrealPakInstaller;
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
        _ = EnsureUnrealPakOnLaunchAsync();

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
        var dialog = new OpenFolderDialog { Title = "Select your classic IMM folder (the one containing Extracted_Mods)" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ImmFolderPath = dialog.FolderName;
        MigrationStatusMessage = ImmInstallPaths.LooksLikeInstallRoot(dialog.FolderName)
            ? "Classic IMM folder set — now pick its mod list."
            : $"Careful: that folder has no Extracted_Mods + {ImmExtractedMods.FileName} in it, so it may not be a classic IMM install.";
    }

    public ObservableCollection<string> MigrationResults { get; } = [];

    public bool HasMigrationResults => MigrationResults.Count > 0;

    /// <summary>
    /// One-click migration: pick a classic IMM merge list, and every mod it names is copied out of
    /// IMM's own Extracted_Mods folder into this Library, linked to its Database or Nexus source
    /// where that can be determined, and finally queued in Merge &amp; Install in the list's own
    /// order — so the user's existing merge setup comes across whole instead of being rebuilt by
    /// hand.
    /// </summary>
    [RelayCommand]
    private async Task ImportImmModListAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick your classic IMM mod list (LastMergedMods.txt or IMM_Merged_Mod.txt)",
            Filter = "Mod list (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // An explicitly-chosen folder always wins — nobody's IMM install has to live where anyone
        // else's does, and auto-detection only works when the list happens to sit inside it.
        var installRoot = !string.IsNullOrWhiteSpace(ImmFolderPath) && ImmInstallPaths.LooksLikeInstallRoot(ImmFolderPath)
            ? ImmFolderPath
            : ImmInstallPaths.FindInstallRoot(dialog.FileName);

        if (installRoot is null)
        {
            // A list taken from the game's own Paks\mods folder has no IMM install anywhere near
            // it, so the mods themselves can't be found without being told where they live.
            MigrationStatusMessage = "That list isn't inside a classic IMM folder — pick your Icarus Software folder so the mods can be found.";
            var folderDialog = new OpenFolderDialog { Title = "Select your classic IMM folder (the one containing Extracted_Mods)" };
            if (folderDialog.ShowDialog() != true)
            {
                MigrationStatusMessage = null;
                return;
            }

            if (!ImmInstallPaths.LooksLikeInstallRoot(folderDialog.FolderName))
            {
                MigrationStatusMessage = $"'{folderDialog.FolderName}' doesn't look like a classic IMM folder — it needs an Extracted_Mods folder and an {ImmExtractedMods.FileName} file.";
                return;
            }

            installRoot = folderDialog.FolderName;
        }

        // Shown back in the box so it's visible what was used, and reusable next time.
        ImmFolderPath = installRoot;

        IsMigratingFromImm = true;
        MigrationResults.Clear();
        OnPropertyChanged(nameof(HasMigrationResults));

        try
        {
            var progress = new Progress<string>(message => MigrationStatusMessage = message);
            var result = await _immMigrationService.MigrateAsync(dialog.FileName, installRoot, progress);

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
            mergeInstall.ReloadLibrary();
            mergeInstall.LoadModListFromFile(dialog.FileName);

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
                + " Your merge list is now queued in Merge & Install.";
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

            Process.Start(new ProcessStartInfo(_customSkinStore.FilePath) { UseShellExecute = true });
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
            _ = VerifyUnrealPakAsync();
        }
    }

    // --- UnrealPak locate-or-install ---

    [ObservableProperty]
    private string? _unrealPakStatusMessage;

    public bool CanInstallBundledUnrealPak => _unrealPakInstaller.PayloadAvailable;

    /// <summary>Label flips to Reinstall once the bundled copy is what's configured — same button, and reinstalling IS the repair path for a broken copy.</summary>
    public string InstallUnrealPakButtonLabel =>
        string.Equals(UnrealPakExePath, _unrealPakInstaller.InstalledExePath, StringComparison.OrdinalIgnoreCase)
            ? "Reinstall bundled copy"
            : "Install bundled copy";

    partial void OnUnrealPakExePathChanged(string? value) => OnPropertyChanged(nameof(InstallUnrealPakButtonLabel));

    /// <summary>
    /// Really runs the exe (see UnrealPakInstaller.VerifyAsync) — deliberately NOT a "check for
    /// updates" against anything newer: UnrealPak is engine-version-specific, Icarus is a UE 4.27
    /// title, and a newer UE5 UnrealPak writes pak formats the game's engine can't mount. The
    /// property that matters is "4.x and intact", which is exactly what this reports.
    /// </summary>
    [RelayCommand]
    private async Task VerifyUnrealPakAsync()
    {
        if (string.IsNullOrWhiteSpace(UnrealPakExePath))
        {
            UnrealPakStatusMessage = CanInstallBundledUnrealPak
                ? "No UnrealPak.exe set — install the bundled copy, or Browse… to one you already have."
                : "No UnrealPak.exe set — Browse… to one you already have (e.g. classic IMM's own, under its UnrealPak folder).";
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
            _ => $"Broken copy: {result.Detail} " + (CanInstallBundledUnrealPak ? "Reinstall bundled copy fixes this." : "Point at a fresh copy."),
        };
    }

    [RelayCommand]
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

    /// <summary>
    /// First-launch (or any launch with no working UnrealPak): silently adopt a copy that's
    /// already installed next to the app, else offer to install the bundled payload, else point
    /// the user at Browse — the "verify, locate, or install next to the exe" flow. Called from
    /// the constructor's launch checks; quiet whenever the configured path simply works.
    /// </summary>
    private async Task EnsureUnrealPakOnLaunchAsync()
    {
        if (!string.IsNullOrWhiteSpace(UnrealPakExePath) && File.Exists(UnrealPakExePath))
        {
            return;
        }

        // A release zip whose Tools folder was pre-extracted (or a previous install) — adopt it
        // without ceremony; there's nothing to ask.
        if (File.Exists(_unrealPakInstaller.InstalledExePath))
        {
            UnrealPakExePath = _unrealPakInstaller.InstalledExePath;
            Save();
            UnrealPakStatusMessage = "Using the copy installed next to the app.";
            return;
        }

        if (_unrealPakInstaller.PayloadAvailable)
        {
            var answer = MessageBox.Show(
                "IcarusStarlink needs UnrealPak.exe to build and unpack mod paks, and none is set up yet.\n\n"
                + "Install the bundled copy next to the app now? (Choose No to point at one you already have, in Settings.)",
                "Set up UnrealPak", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                await InstallBundledUnrealPakAsync();
            }
            else
            {
                UnrealPakStatusMessage = "No UnrealPak.exe set — install the bundled copy, or Browse… to one you already have.";
            }

            return;
        }

        UnrealPakStatusMessage = "No UnrealPak.exe set — Browse… to one you already have (e.g. classic IMM's own, under its UnrealPak folder).";
    }

    [RelayCommand]
    private void Save()
    {
        _settingsService.Current.IcarusContentPath = IcarusContentPath;
        _settingsService.Current.UnrealPakExePath = UnrealPakExePath;
        SavedMessage = _settingsService.Save()
            ? $"Saved at {DateTime.Now:T}"
            : "Failed to save settings — check the logs.";
        WeakReferenceMessenger.Default.Send(new SettingsSavedMessage());
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
    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (LatestAppUpdateRelease is null)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"Download and install v{LatestAppUpdateRelease.Version} now?\n\nThe app will close, update itself, and reopen. Your mods, profiles, and settings are not touched.",
            "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmed == MessageBoxResult.Yes)
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

        IsInstallingAppUpdate = true;
        try
        {
            var token = _credentialStore.Read(CredentialTargets.GitHubToken);
            var workDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"AppUpdate_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDirectory);

            AppUpdateStatusMessage = $"Downloading v{release.Version}…";
            var zipPath = Path.Combine(workDirectory, "update.zip");
            await _appUpdateClient.DownloadAssetAsync(release, token, zipPath);

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
            // files: stage its four files into the temp work folder and launch from there.
            var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var updaterStaging = Path.Combine(workDirectory, "updater");
            Directory.CreateDirectory(updaterStaging);
            string[] updaterFiles = ["IcarusStarlink.Updater.exe", "IcarusStarlink.Updater.dll", "IcarusStarlink.Updater.runtimeconfig.json", "IcarusStarlink.Updater.deps.json"];
            foreach (var fileName in updaterFiles)
            {
                var source = Path.Combine(installDirectory, fileName);
                if (!File.Exists(source))
                {
                    AppUpdateStatusMessage = $"Couldn't stage the updater — {fileName} is missing next to the app.";
                    return;
                }

                File.Copy(source, Path.Combine(updaterStaging, fileName));
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

    /// <summary>The manual fallback next to the automated Update now — opens the release's own GitHub page in the browser.</summary>
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

        // "What's new" — GitHub's own latest release matches the exact version we're running,
        // meaning the app itself was updated since we last recorded a seen version. Deliberately
        // silent when LastSeenAppVersion is still null (a fresh install, or an existing user's
        // first launch after this field was introduced) — only a genuine detected version change
        // shows the dialog, so it never appears out of nowhere on an unrelated launch.
        if (string.Equals(release.Version, InstalledAppVersion, StringComparison.OrdinalIgnoreCase))
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

        if (!IsAppUpdateAvailable)
        {
            return;
        }

        var result = MessageBox.Show(
            $"IcarusStarlink v{release.Version} is available — you're on v{InstalledAppVersion}.\n\nDownload and install it now? The app will close, update itself, and reopen — your mods, profiles, and settings are not touched.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            await InstallAppUpdateAsync();
        }
    }
}
