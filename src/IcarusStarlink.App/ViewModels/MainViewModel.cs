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

        // Matches the real app's top-level nav: Profiles lives inside Merge & Install (a
        // profile is "saved merge list + options + UE4SS set", not an independent page), UE4SS
        // shows up as a sub-tab of Library plus a per-profile section of Merge & Install, and
        // Diagnostics is a section inside Settings — none of the three get their own nav item.
        NavItems =
        [
            new NavItem("downloads", "Downloads", typeof(DownloadsViewModel)),
            // Its own top-level item rather than a Downloads sub-tab, per explicit user request
            // (Phase 8.3) — grouped right next to Downloads since both are mod-sourcing pages.
            new NavItem("nexus", "Nexus", typeof(NexusBrowserViewModel)),
            new NavItem("library", "Library", typeof(LibraryViewModel)),
            new NavItem("merge", "Merge & Install", typeof(MergeInstallViewModel)),
            new NavItem("weekly-changes", "Weekly Changes", typeof(WeeklyChangesViewModel)),
            new NavItem("settings", "Settings", typeof(SettingsViewModel)),
        ];

        _currentThemeName = settingsService.Current.ThemeName;

        // Deliberately not selecting a default page here: the first page (Library) resolves a
        // repository whose constructor scans Extracted_Mods, and this constructor runs before
        // the main window is shown — App.xaml.cs calls SelectDefaultPage() after Show() instead,
        // deferred to the next dispatcher cycle, so the window actually paints first.
    }

    /// <summary>
    /// Defaults to Library specifically, not NavItems[0] — Downloads (now first, matching the
    /// real app's nav order) is still an empty placeholder page until Phase 4, and landing there
    /// on launch would be a worse first impression than the one page that's actually functional.
    /// </summary>
    public void SelectDefaultPage()
    {
        SelectedNavItem = NavItems.First(item => item.Id == "library");
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
