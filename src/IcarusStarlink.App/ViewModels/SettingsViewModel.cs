using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Settings;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    public string Title => "Settings";

    [ObservableProperty]
    private string? _icarusContentPath;

    [ObservableProperty]
    private string? _unrealPakExePath;

    [ObservableProperty]
    private string? _savedMessage;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
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
}
