using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Navigation;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IThemeService _themeService;
    private readonly ISettingsService _settingsService;

    public ObservableCollection<NavItem> NavItems { get; }

    public IReadOnlyList<string> AvailableThemes => _themeService.AvailableThemes;

    [ObservableProperty]
    private NavItem? _selectedNavItem;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _currentThemeName;

    public MainViewModel(IServiceProvider serviceProvider, IThemeService themeService, ISettingsService settingsService)
    {
        _serviceProvider = serviceProvider;
        _themeService = themeService;
        _settingsService = settingsService;

        NavItems =
        [
            new NavItem("library", "Library", typeof(LibraryViewModel)),
            new NavItem("merge", "Merge & Install", typeof(MergeInstallViewModel)),
            new NavItem("downloads", "Downloads", typeof(DownloadsViewModel)),
            new NavItem("profiles", "Profiles", typeof(ProfilesViewModel)),
            new NavItem("ue4ss", "UE4SS", typeof(Ue4ssViewModel)),
            new NavItem("settings", "Settings", typeof(SettingsViewModel)),
            new NavItem("diagnostics", "Diagnostics", typeof(DiagnosticsViewModel)),
        ];

        _currentThemeName = settingsService.Current.ThemeName;

        // Deliberately not selecting a default page here: the first page (Library) resolves a
        // repository whose constructor scans Extracted_Mods, and this constructor runs before
        // the main window is shown — App.xaml.cs calls SelectDefaultPage() after Show() instead,
        // deferred to the next dispatcher cycle, so the window actually paints first.
    }

    public void SelectDefaultPage()
    {
        SelectedNavItem = NavItems[0];
    }

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentPage = _serviceProvider.GetRequiredService(value.ViewModelType);
    }

    [RelayCommand]
    private void SelectTheme(string themeName)
    {
        CurrentThemeName = themeName;
        _themeService.ApplyTheme(themeName);
        _settingsService.Current.ThemeName = themeName;
        _settingsService.Save();
    }
}
