using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Pak;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IUnrealPakService _unrealPakService;
    private readonly IWeeklyChangeReportStore _weeklyChangeReportStore;
    private readonly ISteamInstallLocator _steamInstallLocator;
    private readonly string _dataOutputDirectory;

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

    public SettingsViewModel(
        ISettingsService settingsService, IUnrealPakService unrealPakService, IWeeklyChangeReportStore weeklyChangeReportStore,
        ISteamInstallLocator steamInstallLocator, string dataOutputDirectory)
    {
        _settingsService = settingsService;
        _unrealPakService = unrealPakService;
        _weeklyChangeReportStore = weeklyChangeReportStore;
        _steamInstallLocator = steamInstallLocator;
        _dataOutputDirectory = dataOutputDirectory;
        _icarusContentPath = settingsService.Current.IcarusContentPath;
        _unrealPakExePath = settingsService.Current.UnrealPakExePath;

        // Fire-and-forget, same shape as DownloadsViewModel's constructor-triggered
        // RefreshCatalogAsync: constructors can't be async, and CheckForGameUpdateAsync has its
        // own top-level try/catch so nothing here can produce an unobserved exception.
        _ = CheckForGameUpdateAsync();
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
}
