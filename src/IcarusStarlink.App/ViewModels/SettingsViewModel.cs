using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Pak;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IUnrealPakService _unrealPakService;
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

    public SettingsViewModel(ISettingsService settingsService, IUnrealPakService unrealPakService, string dataOutputDirectory)
    {
        _settingsService = settingsService;
        _unrealPakService = unrealPakService;
        _dataOutputDirectory = dataOutputDirectory;
        _icarusContentPath = settingsService.Current.IcarusContentPath;
        _unrealPakExePath = settingsService.Current.UnrealPakExePath;
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
            var result = await _unrealPakService.ExtractDataPakAsync(UnrealPakExePath, IcarusContentPath, _dataOutputDirectory);
            DataFolderStatusMessage = $"Extracted {result.ExtractedFileCount} files to {_dataOutputDirectory}.";
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
}
