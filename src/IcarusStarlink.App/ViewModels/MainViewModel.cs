using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Navigation;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using Microsoft.Extensions.DependencyInjection;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IThemeService _themeService;
    private readonly ISettingsService _settingsService;

    public ObservableCollection<NavItem> NavItems { get; }

    public ActivityPanelViewModel ActivityPanel { get; }

    /// <summary>Shown in the header next to the app name — same InformationalVersion source Settings' "App updates" section reads (the +sha suffix is already suppressed in the csproj).</summary>
    public string AppVersionDisplay { get; } =
        $"v{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?"}";

    public IReadOnlyList<string> AvailableThemes => _themeService.AvailableThemes;

    [ObservableProperty]
    private NavItem? _selectedNavItem;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _currentThemeName;

    public MainViewModel(IServiceProvider serviceProvider, IThemeService themeService, ISettingsService settingsService, ActivityPanelViewModel activityPanel)
    {
        _serviceProvider = serviceProvider;
        _themeService = themeService;
        _settingsService = settingsService;
        ActivityPanel = activityPanel;

        // Matches the real app's top-level nav: Profiles lives inside Merge & Install (a
        // profile is "saved merge list + options + UE4SS set", not an independent page), UE4SS
        // shows up as a sub-tab of Library plus a per-profile section of Merge & Install, and
        // Diagnostics is a section inside Settings — none of the three get their own nav item.
        // Downloads isn't its own nav item either any more — its IMM Database tab and its
        // pending-downloads list both moved onto Library itself (a downloaded mod already blends
        // straight into Library's own list once it finishes, so a separate "Downloads" page just
        // duplicated the same job).
        NavItems =
        [
            // Its own top-level item rather than a Downloads sub-tab, per explicit user request
            // (Phase 8.3).
            new NavItem("nexus", "Nexus", typeof(NexusCatalogViewModel)),
            new NavItem("library", "Library", typeof(LibraryViewModel)),
            new NavItem("merge", "Merge & Install", typeof(MergeInstallViewModel)),
            new NavItem("weekly-changes", "Weekly Changes", typeof(WeeklyChangesViewModel)),
            // A narrower revival of the spec's original "Server" nav item, cut from v1 entirely —
            // FTP file management for a dedicated server's mods folder (Phase 8.5), not the full
            // remote-agent/control page the original spec described. Marked Beta per the user's
            // own call — confirmed live against a real SurvivalServers account that the FTP
            // delete/overwrite this page's own "Install merged pak"/basic Delete actions need is
            // blocked account-wide on at least one real host, so the page's core install-to-server
            // path isn't reliably functional yet.
            new NavItem("server", "Server (Beta)", typeof(ServerViewModel)),
            // Between Server and Settings, matching the real Icarus Workshop's own nav order.
            // Marked Beta while the deeper editing tabs (cosmetics, items, bestiary...) are
            // still landing — per the user's own call before the first release carries it.
            new NavItem("saves", "Saves (Beta)", typeof(SavesViewModel)),
            new NavItem("settings", "Settings", typeof(SettingsViewModel)),
            new NavItem("help", "Help", typeof(HelpViewModel)),
        ];

        _currentThemeName = settingsService.Current.ThemeName;

        // Lets a page hand the user off to another one (Library's "Find in Database" / "Search
        // Nexus for this"). Registered on the shell rather than handled by the sender because only
        // the shell owns which page is showing.
        WeakReferenceMessenger.Default.Register<NavigateToPageMessage>(this, (recipient, message) =>
        {
            var viewModel = (MainViewModel)recipient;
            var target = viewModel.NavItems.FirstOrDefault(item => item.Id == message.NavItemId);
            if (target is not null)
            {
                viewModel.SelectedNavItem = target;
            }
        });

        // Deliberately not selecting a default page here: the first page (Library) resolves a
        // repository whose constructor scans Extracted_Mods, and this constructor runs before
        // the main window is shown — App.xaml.cs calls SelectDefaultPage() after Show() instead,
        // deferred to the next dispatcher cycle, so the window actually paints first.

        // OpenUe4ssFolder/OpenGameFolder's own CanExecute reads IcarusContentPath, which isn't
        // itself observable from here — without this, setting the Content path for the first time
        // in Settings would leave both quick-links looking permanently disabled until next launch.
        WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (recipient, _) =>
        {
            var viewModel = (MainViewModel)recipient;
            viewModel.OpenUe4ssFolderCommand.NotifyCanExecuteChanged();
            viewModel.OpenGameFolderCommand.NotifyCanExecuteChanged();
        });
    }

    /// <summary>Library's own Extracted_Mods folder — always exists once the app has run once (FolderLibraryRepository creates it on startup), created here defensively too so this can never open a "folder not found" error.</summary>
    [RelayCommand]
    private void OpenModFolder()
    {
        var modFolder = Path.Combine(AppContext.BaseDirectory, "Extracted_Mods");
        Directory.CreateDirectory(modFolder);
        UrlOpener.TryOpen(modFolder);
    }

    private bool CanOpenGameFolders() => !string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath);

    [RelayCommand(CanExecute = nameof(CanOpenGameFolders))]
    private void OpenUe4ssFolder() => UrlOpener.TryOpen(Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath!));

    /// <summary>The real game install root (Icarus\Icarus, a sibling of Content) — same TrimEnd/GetDirectoryName Ue4ssGamePaths itself already uses to derive it, since IcarusContentPath is the only path this app actually keeps.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenGameFolders))]
    private void OpenGameFolder()
    {
        var trimmed = _settingsService.Current.IcarusContentPath!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetDirectoryName(trimmed) is { } gameRoot)
        {
            UrlOpener.TryOpen(gameRoot);
        }
    }

    /// <summary>Steam's own URI launch, not the exe directly — respects whatever launch options/overlay the user already has configured in Steam, the same way clicking Play in the Steam library itself does. 1149460 is Icarus's real App ID, already used by SteamInstallLocator elsewhere in this app.</summary>
    [RelayCommand]
    private void LaunchGame() => UrlOpener.TryOpen("steam://run/1149460");

    /// <summary>
    /// Defaults to Library specifically, not NavItems[0] — Downloads (now first, matching the
    /// real app's nav order) is still an empty placeholder page until Phase 4, and landing there
    /// on launch would be a worse first impression than the one page that's actually functional.
    /// Only sets it if nothing else already has — both this and a real nxm:// handoff's own nav
    /// switch to Downloads are queued via Dispatcher.BeginInvoke from App.xaml.cs's OnStartup, and
    /// which one the dispatcher actually runs first is a genuine race (the handoff arrives on a
    /// background named-pipe thread, this one is queued synchronously near the end of OnStartup) —
    /// without this guard, the handoff arriving first would just get silently clobbered back to
    /// Library a moment later.
    /// </summary>
    public void SelectDefaultPage()
    {
        if (SelectedNavItem is null)
        {
            SelectedNavItem = NavItems.First(item => item.Id == "library");
        }
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
