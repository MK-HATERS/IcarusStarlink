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
using IcarusStarlink.Core.Server;
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
    [NotifyPropertyChangedFor(nameof(NextThemeName), nameof(ThemeButtonText), nameof(ThemeButtonToolTip))]
    private string _currentThemeName;

    /// <summary>The theme the one-click Theme button (bottom of the menu) switches to: the next in the list, round to the first.</summary>
    public string NextThemeName
    {
        get
        {
            var index = AvailableThemes.ToList().FindIndex(t => string.Equals(t, CurrentThemeName, StringComparison.OrdinalIgnoreCase));
            return AvailableThemes.Count == 0 ? CurrentThemeName : AvailableThemes[(index + 1) % AvailableThemes.Count];
        }
    }

    public string ThemeButtonText => $"Theme: {CurrentThemeName}";

    /// <summary>
    /// The Theme button's tooltip. Built here, not with StringFormat in the XAML: ToolTip isn't a
    /// text property, so WPF ignores StringFormat on it and the tooltip said only "Dark".
    /// </summary>
    public string ThemeButtonToolTip => $"Change the colours: click for the {NextThemeName} theme. Each click goes to the next one.";

    /// <summary>The Theme button: Icarus → Dark → Light → Custom → Icarus, one step per click.</summary>
    [RelayCommand]
    private void NextTheme() => SelectTheme(NextThemeName);


    /// <summary>
    /// The left navigation folded to a strip of icons (user request 2026-10-23): every item keeps its
    /// icon and its name as a tooltip, so it still says what's what. Remembered in AppSettings.NavCollapsed.
    /// </summary>
    public bool IsNavCollapsed
    {
        get => _settingsService.Current.NavCollapsed;
        set
        {
            if (_settingsService.SetAndSave(s => s.NavCollapsed, (s, v) => s.NavCollapsed = v, value))
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(NavToggleToolTip));
            }
        }
    }

    public string NavToggleToolTip => IsNavCollapsed ? "Show the menu's names" : "Fold the menu to icons";

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleNav() => IsNavCollapsed = !IsNavCollapsed;

    public MainViewModel(IServiceProvider serviceProvider, IThemeService themeService, ISettingsService settingsService, ActivityPanelViewModel activityPanel,
        IGameProcessChecker? gameProcess = null, Func<DateTime>? clock = null, Action<string>? openUrl = null, Func<bool>? focusGame = null)
    {
        _serviceProvider = serviceProvider;
        _themeService = themeService;
        _settingsService = settingsService;
        ActivityPanel = activityPanel;
        UseGameSeams(gameProcess, clock, openUrl, focusGame);

        // Matches the real app's top-level nav: Profiles lives inside Merge & Install (a
        // profile is "saved merge list + options + UE4SS set", not an independent page), UE4SS
        // shows up as a sub-tab of Library plus a per-profile section of Merge & Install, and
        // Diagnostics is a section inside Settings — none of the three get their own nav item.
        // Downloads isn't its own nav item either any more — its IMM Database tab and its
        // pending-downloads list both moved onto Library itself (a downloaded mod already blends
        // straight into Library's own list once it finishes, so a separate "Downloads" page just
        // duplicated the same job).
        // Grouped by what the user is doing (their ask, 2026-10-23: "the left column could be better
        // organized"): finding mods, working on their own, the game itself, then the app.
        NavItems =
        [
            // Nexus and the IMM Database together — everywhere mods can be found ("nexus" and
            // "imm-database" hand-offs land on its tabs).
            new NavItem("database", "Database", typeof(DatabaseViewModel), "Find mods"),
            // What was downloaded and what became of it — its own page, like every mod organizer.
            new NavItem("downloads", "Downloads", typeof(DownloadsViewModel), "Find mods"),
            new NavItem("library", "Library", typeof(LibraryViewModel), "Your mods"),
            // Changing the game's own items (New mod is for new ones) — the user's own name for it.
            new NavItem("editor", "Editor", typeof(GameEditorViewModel), "Your mods"),
            new NavItem("merge", "Merge & Install", typeof(MergeInstallViewModel), "Your mods"),
            // Out of Beta at the user's call (2026-10-23), with the save check, mount editor and points.
            new NavItem("saves", "Saves", typeof(SavesViewModel), "Game"),
            // FTP file management for a rented server's mods folder plus the Local server tab.
            new NavItem("server", "Server", typeof(ServerViewModel), "Game"),
            // Every world's map with its creature, fishing and biome zones, read from the game's files.
            new NavItem("map", "Map", typeof(WorldMapViewModel), "Game"),
            new NavItem("weekly-changes", "Weekly Changes", typeof(WeeklyChangesViewModel), "Game"),
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
            var id = message.NavItemId is "nexus" or "imm-database" ? "database" : message.NavItemId;
            var target = viewModel.NavItems.FirstOrDefault(item => item.Id == id);
            if (target is not null)
            {
                viewModel.SelectedNavItem = target;
            }

            // Pick the Database tab here, after the page is resolved: the first time it's opened,
            // DatabaseViewModel is only built during this very send, so its own handler registers
            // too late to hear it and "Find in Database" landed on the Nexus tab.
            if (viewModel.CurrentPage is DatabaseViewModel database)
            {
                if (message.NavItemId == "nexus")
                {
                    database.SelectedTab = DatabaseViewModel.NexusTab;
                }
                else if (message.NavItemId == "imm-database")
                {
                    database.SelectedTab = DatabaseViewModel.ImmDatabaseTab;
                }
            }
        });

        // "Find on Nexus" from the Downloads page. Handled here, not on NexusCatalogViewModel,
        // which is only built the first time the Database page opens — before that nobody heard it.
        WeakReferenceMessenger.Default.Register<SearchNexusMessage>(this, (recipient, message) =>
        {
            var viewModel = (MainViewModel)recipient;
            if (viewModel._serviceProvider.GetService(typeof(NexusCatalogViewModel)) is NexusCatalogViewModel nexus)
            {
                nexus.SearchText = message.SearchText;
            }

            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
        });

        // "Turn on and go to Merge & Install" after a save: the shell owns both the page and the queue's VM.
        WeakReferenceMessenger.Default.Register<TurnOnModMessage>(this, (recipient, message) =>
        {
            var viewModel = (MainViewModel)recipient;
            viewModel._serviceProvider.GetRequiredService<MergeInstallViewModel>().AddToQueueByFolderNames([message.FolderName]);
            viewModel.SelectedNavItem = viewModel.NavItems.First(item => item.Id == "merge");
            System.Windows.Application.Current?.MainWindow?.Activate();
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

    /// <summary>
    /// The local dedicated server's install folder (the first one set up on the Server page). With
    /// no server yet there's nothing to open, so it goes to the Server page, where one is set up.
    /// </summary>
    [RelayCommand]
    private void OpenServerFolder()
    {
        var server = _serviceProvider.GetService<IDedicatedServerProfileStore>()?.GetAll().FirstOrDefault(p => Directory.Exists(p.InstallFolder));
        if (server is not null)
        {
            UrlOpener.TryOpen(server.InstallFolder);
            return;
        }

        SelectedNavItem = NavItems.First(item => item.Id == "server");
    }

    /// <summary>
    /// Defaults to Library, not NavItems[0] (the first nav item is Database). Only sets it if
    /// nothing else already has: a real nxm:// handoff's switch to Downloads arrives on a
    /// background named-pipe thread while startup is still finishing, and without this guard it
    /// could be clobbered back to Library a moment later.
    ///
    /// A real bug found live: a genuinely first-ever launch (IcarusContentPath never set) still
    /// landed on Library — an empty list, with nothing on screen explaining that Settings needs
    /// the Content folder configured first. Help's own "Getting started" topic already walks
    /// through exactly that in the right order (Settings → Content folder → Auto-detect →
    /// UnrealPak → Update data folder → Library import), so a brand-new user is routed there
    /// instead this one time — every subsequent launch (IcarusContentPath now set, however it got
    /// set) goes back to Library as before.
    /// </summary>
    public void SelectDefaultPage()
    {
        if (SelectedNavItem is null)
        {
            var isFirstRun = string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath);
            SelectedNavItem = NavItems.First(item => item.Id == (isFirstRun ? "help" : "library"));
        }
    }

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentPage = _serviceProvider.GetRequiredService(value.ViewModelType);

        // The merged pak may have been (re)built, and UE4SS mods turned on in the Library, since the
        // Server page was last shown.
        if (CurrentPage is ServerViewModel { Local: { } localServer })
        {
            localServer.OnPageShown();
        }

        if (CurrentPage is LibraryViewModel library)
        {
            library.OnPageShown();
        }

        // The game may have written newer progress while another page was open.
        if (CurrentPage is SavesViewModel saves)
        {
            saves.OnPageShown();
        }

        // A game update or a changed mod may have changed the maps since they were last shown.
        if (CurrentPage is WorldMapViewModel map)
        {
            map.OnPageShown();
        }

        // Installs and fixes since it was last shown made new backups.
        if (CurrentPage is SettingsViewModel settings)
        {
            settings.RefreshBackupsSize();
        }
    }

    /// <summary>The Database page once it has been shown this session (its New tags are recorded as seen on close); null if never opened.</summary>
    private DatabaseViewModel? _databaseShownThisSession;

    partial void OnCurrentPageChanged(object? oldValue, object? newValue)
    {
        if (oldValue is DatabaseViewModel leftDatabase && !ReferenceEquals(oldValue, newValue))
        {
            leftDatabase.OnPageLeft();
        }

        if (newValue is DatabaseViewModel database)
        {
            _databaseShownThisSession = database;
            database.OnPageShown();
        }
    }

    /// <summary>
    /// Starlink is closing (MainWindow.Closing, after any "close anyway?" question): the Database
    /// tabs opened this session record their mods as seen, so next time only newer ones say New.
    /// Never builds the Database page just for this. Never throws.
    /// </summary>
    public void OnAppClosing()
    {
        try
        {
            _databaseShownThisSession?.RecordSeen();
        }
        catch (Exception)
        {
            // Best-effort: the New tags just show again next time.
        }
    }

    [RelayCommand]
    private void SelectTheme(string themeName)
    {
        CurrentThemeName = themeName;
        _themeService.ApplyTheme(themeName);
        _settingsService.Current.ThemeName = themeName;
        _settingsService.Save();
    }

    /// <summary>Ctrl+1 … Ctrl+9 (Ctrl+0 = 10): the menu's pages in order. False when there's no such page.</summary>
    public bool GoToPageNumber(int number)
    {
        if (number < 1 || number > NavItems.Count)
        {
            return false;
        }

        SelectedNavItem = NavItems[number - 1];
        return true;
    }
}
