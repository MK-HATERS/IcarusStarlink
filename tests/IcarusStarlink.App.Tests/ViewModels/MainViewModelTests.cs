using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Covers SelectDefaultPage's own first-run routing and the page hand-offs (which Database tab a
/// "nexus" / "imm-database" hand-off lands on) — theme switching and the settings-saved
/// CanExecute refresh aren't exercised here.
/// </summary>
[Collection(MessengerCollection.Name)]
public class MainViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static MainViewModel Build(string? icarusContentPath, IServiceProvider? services = null)
    {
        var settings = new AppSettings { IcarusContentPath = icarusContentPath };
        var vm = new MainViewModel(
            services ?? new FakeServiceProvider(), new FakeThemeService(), new FakeSettingsService(settings),
            new ActivityPanelViewModel(new FakeActivityLog()));
        return vm;
    }

    /// <summary>A view model whose folder and link opening is recorded (and fails with <paramref name="openFails"/>), and whose warnings land in <paramref name="log"/>.</summary>
    private static MainViewModel BuildWithSeams(string? icarusContentPath, List<string> opened, FakeActivityLog log,
        Exception? openFails = null, IGameProcessChecker? game = null, BrokenModSearchStore? findBrokenModSearch = null) =>
        new(new FakeServiceProvider(), new FakeThemeService(), new FakeSettingsService(new AppSettings { IcarusContentPath = icarusContentPath }),
            new ActivityPanelViewModel(new FakeActivityLog()), game ?? new FakeGameProcess(), () => DateTime.UtcNow,
            url => { opened.Add(url); return openFails; }, () => true, findBrokenModSearch, log);

    /// <summary>
    /// A real bug: the first "Find in Database" of a session opened the Nexus tab — the Database
    /// page is only built during the hand-off itself, too late for its own handler to hear it.
    /// </summary>
    [Fact]
    public void FirstHandoffToImmDatabase_BuildsThePageAndPicksItsTab()
    {
        var services = new LazyDatabaseServiceProvider();
        var vm = Build(@"C:\fake\Icarus\Content", services);
        try
        {
            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("imm-database"));

            Assert.Equal("database", vm.SelectedNavItem?.Id);
            var database = Assert.IsType<DatabaseViewModel>(vm.CurrentPage);
            Assert.Equal(DatabaseViewModel.ImmDatabaseTab, database.SelectedTab);

            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
            Assert.Equal(DatabaseViewModel.NexusTab, database.SelectedTab);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
            if (services.Database is { } built)
            {
                WeakReferenceMessenger.Default.UnregisterAll(built);
            }
        }
    }

    /// <summary>"Find on Nexus" from Downloads works before the Database page was ever opened.</summary>
    [Fact]
    public void SearchNexus_BeforeTheDatabasePageWasOpened_StillGoesToTheNexusTab()
    {
        var services = new LazyDatabaseServiceProvider();
        var vm = Build(@"C:\fake\Icarus\Content", services);
        vm.SelectedNavItem = vm.NavItems.First(item => item.Id == "downloads");
        try
        {
            WeakReferenceMessenger.Default.Send(new SearchNexusMessage("StorageLink"));

            Assert.Equal("database", vm.SelectedNavItem?.Id);
            Assert.Equal(DatabaseViewModel.NexusTab, Assert.IsType<DatabaseViewModel>(vm.CurrentPage).SelectedTab);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
            if (services.Database is { } built)
            {
                WeakReferenceMessenger.Default.UnregisterAll(built);
            }
        }
    }

    [Fact]
    public void SelectDefaultPage_NeverConfigured_LandsOnHelpNotLibrary()
    {
        // A real bug found live: a brand-new install (no Content folder set yet) landed on an
        // empty Library page with no on-screen guidance — Help's own "Getting started" topic
        // already walks through the right setup order, so a genuinely first-ever launch should
        // land there instead.
        var vm = Build(icarusContentPath: null);

        vm.SelectDefaultPage();

        Assert.Equal("help", vm.SelectedNavItem?.Id);
    }

    [Fact]
    public void SelectDefaultPage_ContentPathAlreadyConfigured_LandsOnLibraryAsBefore()
    {
        var vm = Build(icarusContentPath: @"C:\fake\Icarus\Content");

        vm.SelectDefaultPage();

        Assert.Equal("library", vm.SelectedNavItem?.Id);
    }

    [Fact]
    public void SelectDefaultPage_SomethingAlreadySelectedNavItem_DoesNotOverrideIt()
    {
        var vm = Build(icarusContentPath: null);
        vm.SelectedNavItem = vm.NavItems.First(item => item.Id == "database");

        vm.SelectDefaultPage();

        Assert.Equal("database", vm.SelectedNavItem?.Id);
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        // OnSelectedNavItemChanged resolves the page's real ViewModel via this — these tests only
        // care which NavItem.Id gets picked, not what CurrentPage actually resolves to, so any
        // non-null stand-in satisfies GetRequiredService's own null-check without needing a real
        // DI container or constructing LibraryViewModel/HelpViewModel's own dependencies.
        public object? GetService(Type serviceType) => new object();
    }

    /// <summary>Builds the Database page only when it's first asked for, the way the real DI singleton is.</summary>
    private sealed class LazyDatabaseServiceProvider : IServiceProvider
    {
        public DatabaseViewModel? Database { get; private set; }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(DatabaseViewModel) ? Database ??= new DatabaseViewModel(null!, null!)
            : serviceType == typeof(NexusCatalogViewModel) ? null
            : new object();
    }

    /// <summary>The Theme button (bottom of the menu) steps through every theme, back round to the first, and saves each.</summary>
    [Fact]
    public void ThemeButton_StepsThroughEveryTheme_AndRoundToTheFirst()
    {
        var settings = new AppSettings { IcarusContentPath = @"C:\fake", ThemeName = "Icarus" };
        var themes = new FakeThemeService();
        var vm = new MainViewModel(new FakeServiceProvider(), themes, new FakeSettingsService(settings), new ActivityPanelViewModel(new FakeActivityLog()));
        try
        {
            var seen = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal($"Theme: {vm.CurrentThemeName}", vm.ThemeButtonText);
                Assert.Equal($"Change the colours: click for the {vm.NextThemeName} theme. Each click goes to the next one.", vm.ThemeButtonToolTip);
                vm.NextThemeCommand.Execute(null);
                seen.Add(vm.CurrentThemeName);
            }

            Assert.Equal(["Dark", "Light", "Custom", "Icarus"], seen);
            Assert.Equal("Icarus", themes.Applied);
            Assert.Equal("Icarus", settings.ThemeName);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Ctrl+1 … Ctrl+9 go to the menu's first nine pages in order; Ctrl+0 is always Help (regression: the Map page pushed Help to 11th and Ctrl+0 opened Settings).</summary>
    [Fact]
    public void GoToPageNumber_GoesToThatMenuEntry()
    {
        var vm = Build(@"C:\fake\Icarus\Content");
        try
        {
            Assert.True(vm.GoToPageNumber(1));
            Assert.Equal(vm.NavItems[0], vm.SelectedNavItem);
            Assert.True(vm.GoToPageNumber(3));
            Assert.Equal(vm.NavItems[2], vm.SelectedNavItem);
            Assert.False(vm.GoToPageNumber(11));
            Assert.Equal(vm.NavItems[2], vm.SelectedNavItem);
            Assert.True(vm.GoToPageNumber(10));
            Assert.Equal("help", vm.SelectedNavItem!.Id);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: going from Map to another page never told the Map page, so its images (100+ MB on Olympus) were never let go while idle.</summary>
    [Fact]
    public void LeavingTheMapPage_LetsItsImagesGoWhenIdle()
    {
        var map = new WorldMapInfo("A", "Alpha", false, [], [],
            [new WorldLayer(WorldLayerKind.Creatures, "Heatmaps/x.uasset",
                new Dictionary<uint, WorldZone> { [0xAA0000] = new("OLY_A", "A", []), [0x00BB00] = new("OLY_B", "B", []) })]);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [map], new OneLayerRenderer());
        store.Reload(everything: false);
        // No game folder: opening the page says so straight away instead of starting a read.
        var mapPage = new WorldMapViewModel(store, new FakeSettingsService(new AppSettings()), Path.Combine(_root, "Data")) { UiDispatcher = () => null };
        mapPage.SelectedMap = new WorldMapItemViewModel(map);
        Assert.NotEmpty(mapPage.Layers);

        var vm = Build(@"C:\fake\Icarus\Content", new MapServiceProvider(mapPage));
        try
        {
            vm.SelectedNavItem = vm.NavItems.First(item => item.Id == "map");
            Assert.False(mapPage.ReleaseIfIdle(TimeSpan.Zero));

            vm.SelectedNavItem = vm.NavItems.First(item => item.Id == "help");

            Assert.True(mapPage.ReleaseIfIdle(TimeSpan.Zero));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Gives the one Map page for the Map entry and a stand-in for every other page.</summary>
    private sealed class MapServiceProvider(WorldMapViewModel map) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(WorldMapViewModel) ? map : new object();
    }

    /// <summary>No picture; every zone image is the same 2x2 image with two zones.</summary>
    private sealed class OneLayerRenderer : IWorldMapRenderer
    {
        public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map) => (null, WorldPictureSource.None);

        public WorldLayerPixels? ReadLayer(string heatmapAssetPath) => new(2, [0xAA0000, 0x00BB00, 0xAA0000, 0x00BB00]);

        public string Stamp(WorldMapInfo map) => "1";
    }

    /// <summary>
    /// Play could be clicked again and again, asking Steam to start the game each time. A click shows Starting… and further clicks do nothing until the game shows up
    /// (then Running, and a click brings it forward) or 2 minutes pass (then Play again).
    /// </summary>
    [Fact]
    public void Play_StartsOnce_ThenStarting_ThenRunning_AndBackToPlayWhenTheGameCloses()
    {
        var game = new FakeGameProcess();
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var opened = new List<string>();
        var focused = 0;
        var vm = new MainViewModel(new FakeServiceProvider(), new FakeThemeService(), new FakeSettingsService(new AppSettings()),
            new ActivityPanelViewModel(new FakeActivityLog()), game, () => now, url => { opened.Add(url); return null; }, () => { focused++; return true; });
        try
        {
            Assert.Equal("Play", vm.PlayButtonText);

            vm.LaunchGameCommand.Execute(null);
            vm.LaunchGameCommand.Execute(null);
            vm.LaunchGameCommand.Execute(null);
            Assert.Equal(["steam://run/1149460"], opened);
            Assert.Equal(GamePlayState.Starting, vm.GamePlayState);
            Assert.Equal("Starting…", vm.PlayButtonText);

            now += TimeSpan.FromSeconds(30);
            vm.ApplyGameRunning(false);
            Assert.True(vm.IsGameStarting); // still waiting for Steam

            vm.ApplyGameRunning(true);
            Assert.True(vm.IsGameRunning);
            Assert.Equal("Running", vm.PlayButtonText);

            game.Running = true;
            vm.LaunchGameCommand.Execute(null);
            Assert.Single(opened); // not asked again
            Assert.Equal(1, focused);

            game.Running = false;
            vm.ApplyGameRunning(false);
            Assert.Equal(GamePlayState.Ready, vm.GamePlayState);
            Assert.Equal("Play", vm.PlayButtonText);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Steam never started the game (an update, a question, a closed Steam): Play comes back after 2 minutes.</summary>
    [Fact]
    public void Play_StartingTimesOut_AndAGameAlreadyRunningIsNotStartedAgain()
    {
        var game = new FakeGameProcess();
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var opened = new List<string>();
        var vm = new MainViewModel(new FakeServiceProvider(), new FakeThemeService(), new FakeSettingsService(new AppSettings()),
            new ActivityPanelViewModel(new FakeActivityLog()), game, () => now, url => { opened.Add(url); return null; }, () => true);
        try
        {
            vm.LaunchGameCommand.Execute(null);
            now += MainViewModel.StartingTimeout;
            vm.ApplyGameRunning(false);
            Assert.Equal(GamePlayState.Ready, vm.GamePlayState);

            vm.LaunchGameCommand.Execute(null);
            Assert.Equal(2, opened.Count); // a fresh try is allowed after the timeout

            vm.ApplyGameRunning(false);
            now += MainViewModel.StartingTimeout;
            vm.ApplyGameRunning(false);
            game.Running = true; // started outside Starlink, before the watch noticed
            vm.LaunchGameCommand.Execute(null);
            Assert.Equal(2, opened.Count);
            Assert.True(vm.IsGameRunning);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: App.OnStartup starts the game watch from its background startup thread, and the
    /// timer was made on that thread, where no dispatcher ever runs: it never ticked, so Play kept
    /// whatever the first look saw (Starting… for good, or Running after the game closed).
    /// </summary>
    [Fact]
    public async Task GameWatch_StartedOffTheWindowThread_TicksOnTheWindowsDispatcher()
    {
        var vm = new MainViewModel(new FakeServiceProvider(), new FakeThemeService(), new FakeSettingsService(new AppSettings()),
            new ActivityPanelViewModel(new FakeActivityLog()), new FakeGameProcess(), () => DateTime.UtcNow, _ => null, () => true);
        try
        {
            var window = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            vm.UiDispatcher = () => window;

            await Task.Run(vm.StartGameWatch);

            Assert.Same(window, vm.GameWatchDispatcher);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: when Windows couldn't hand steam://run to Steam, the error was dropped and the
    /// button sat on Starting… for 2 minutes with no word.
    /// </summary>
    [Fact]
    public void Play_SteamCantBeAsked_StaysOnPlayAndWarns()
    {
        var opened = new List<string>();
        var log = new FakeActivityLog();
        var vm = BuildWithSeams(null, opened, log, openFails: new System.ComponentModel.Win32Exception(1155));
        try
        {
            vm.LaunchGameCommand.Execute(null);

            Assert.Equal(["steam://run/1149460"], opened);
            Assert.Equal(GamePlayState.Ready, vm.GamePlayState);
            Assert.Contains("Steam", Assert.Single(log.Warnings));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: for up to 3 seconds after Icarus closed or crashed the button still said
    /// Running, and a click only tried to bring the (gone) game forward: nothing started.
    /// </summary>
    [Fact]
    public void Play_RunningButTheGameClosed_StartsItAgain()
    {
        var game = new FakeGameProcess { Running = true };
        var opened = new List<string>();
        var vm = BuildWithSeams(null, opened, new FakeActivityLog(), game: game);
        try
        {
            vm.ApplyGameRunning(true);
            game.Running = false; // closed before the next watch tick

            vm.LaunchGameCommand.Execute(null);

            Assert.Equal(["steam://run/1149460"], opened);
            Assert.Equal(GamePlayState.Starting, vm.GamePlayState);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: with no UE4SS in the game, the header's UE4SS button did nothing and said nothing.</summary>
    [Fact]
    public void OpenUe4ssFolder_Ue4ssNotInstalled_SaysSoAndOpensNothing()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Icarus", "Content")).FullName;
        var opened = new List<string>();
        var log = new FakeActivityLog();
        var vm = BuildWithSeams(content, opened, log);
        try
        {
            vm.OpenUe4ssFolderCommand.Execute(null);

            Assert.Empty(opened);
            Assert.Contains("UE4SS isn't installed", Assert.Single(log.Warnings));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    [Fact]
    public void OpenUe4ssFolder_Installed_OpensItsModsFolder()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Icarus", "Content")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(_root, "Icarus", "Binaries", "Win64", "ue4ss", "Mods")).FullName;
        var opened = new List<string>();
        var log = new FakeActivityLog();
        var vm = BuildWithSeams(content, opened, log);
        try
        {
            vm.OpenUe4ssFolderCommand.Execute(null);

            Assert.Equal([mods], opened);
            Assert.Empty(log.Warnings);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: a game folder set to a drive root (no parent folder) threw out of the command, which closes Starlink.</summary>
    [Fact]
    public void OpenUe4ssFolder_GameFolderIsADriveRoot_SaysSoInsteadOfThrowing()
    {
        var opened = new List<string>();
        var log = new FakeActivityLog();
        var vm = BuildWithSeams(@"C:\", opened, log);
        try
        {
            vm.OpenUe4ssFolderCommand.Execute(null);

            Assert.Empty(opened);
            Assert.Contains("Settings", Assert.Single(log.Warnings));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: after the game was moved or uninstalled, the header's Game button did nothing and said nothing.</summary>
    [Fact]
    public void OpenGameFolder_FolderGone_SaysSo()
    {
        var opened = new List<string>();
        var log = new FakeActivityLog();
        var vm = BuildWithSeams(Path.Combine(_root, "Gone", "Icarus", "Content"), opened, log);
        try
        {
            vm.OpenGameFolderCommand.Execute(null);

            Assert.Empty(opened);
            Assert.Contains("isn't there any more", Assert.Single(log.Warnings));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Windows refusing to open the folder is said, with its reason, instead of dropped.</summary>
    [Fact]
    public void OpenGameFolder_WindowsCantOpenIt_SaysWhy()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "Icarus", "Content")).FullName;
        var opened = new List<string>();
        var log = new FakeActivityLog();
        var vm = BuildWithSeams(content, opened, log, openFails: new InvalidOperationException("Access is denied."));
        try
        {
            vm.OpenGameFolderCommand.Execute(null);

            Assert.Equal([Path.Combine(_root, "Icarus")], opened);
            Assert.Contains("Access is denied.", Assert.Single(log.Warnings));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Settings is the tenth page, past Ctrl+1…9, and Ctrl+0 is Help: Ctrl+, opens it.</summary>
    [Fact]
    public void GoToSettings_OpensTheSettingsPage()
    {
        var vm = Build(@"C:\fake\Icarus\Content");
        try
        {
            Assert.True(vm.GoToSettings());
            Assert.Equal("settings", vm.SelectedNavItem?.Id);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>The folded menu's tooltips say each page's shortcut: Ctrl+1…9 by place, Ctrl+0 Help, Ctrl+, Settings.</summary>
    [Fact]
    public void NavItems_EachSaysItsShortcut()
    {
        var vm = Build(@"C:\fake\Icarus\Content");
        try
        {
            for (var i = 0; i < 9; i++)
            {
                Assert.Equal($"Ctrl+{i + 1}", vm.NavItems[i].Shortcut);
            }

            Assert.Equal("Ctrl+0", vm.NavItems.First(i => i.Id == "help").Shortcut);
            Assert.Equal("Ctrl+,", vm.NavItems.First(i => i.Id == "settings").Shortcut);
            Assert.Equal("Library (Ctrl+3)", vm.NavItems.First(i => i.Id == "library").TitleWithShortcut);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Closing Starlink while Find the broken mod has a round in the game used to ask nothing: Icarus,
    /// started from Steam, then loaded without the other mods. Now it asks first.
    /// </summary>
    [Fact]
    public void Closing_WhileFindTheBrokenModHasSomeModsOut_AsksFirst()
    {
        var store = new BrokenModSearchStore(Path.Combine(_root, "find_broken_mod.json"));
        var vm = BuildWithSeams(null, [], new FakeActivityLog(), findBrokenModSearch: store);
        try
        {
            Assert.False(vm.FindBrokenModHasSomeModsOut()); // no search

            store.Save(new BrokenModSearchRecord { OriginalQueue = ["A", "B"] });
            Assert.False(vm.FindBrokenModHasSomeModsOut()); // started, but the game still has every mod

            store.Save(new BrokenModSearchRecord { OriginalQueue = ["A", "B"], Installed = ["A"] });
            Assert.True(vm.FindBrokenModHasSomeModsOut());

            var question = MainViewModel.CloseQuestion(unsaved: null, running: null, findBrokenModHasSomeModsOut: true);
            Assert.NotNull(question);
            Assert.Contains("Find the broken mod", question);
            Assert.Contains("only some of your mods", question);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    [Fact]
    public void CloseQuestion_NothingAtStake_AsksNothing_AndListsEachReasonOtherwise()
    {
        Assert.Null(MainViewModel.CloseQuestion(null, null, findBrokenModHasSomeModsOut: false));

        var question = MainViewModel.CloseQuestion("the mod editor", "a download", findBrokenModHasSomeModsOut: true);
        Assert.Contains("Not saved yet: the mod editor.", question);
        Assert.Contains("Still running: a download", question);
        Assert.Contains("Find the broken mod", question);
    }

    /// <summary>
    /// The close question for Find the broken mod is left out while WPF shuts Starlink down
    /// (Windows signing out, the updater taking over), when a Cancel can't keep it open. That
    /// check reads WPF's own shutdown flag: this fails if a WPF update renames it.
    /// </summary>
    [Fact]
    public void TheCloseQuestion_CanTellWhenStarlinkIsBeingShutDown() =>
        Assert.True(MainWindow.CanSeeAppShutdown);

    private sealed class FakeGameProcess : IGameProcessChecker
    {
        public bool Running { get; set; }
        public bool IsRunning() => Running;
    }

    private sealed class FakeThemeService : IThemeService
    {
        public string? Applied { get; private set; }
        public IReadOnlyList<string> AvailableThemes => ["Icarus", "Dark", "Light", "Custom"];
        public void ApplyTheme(string themeName) => Applied = themeName;
        public IReadOnlyDictionary<string, string> GetThemeColors(string themeName) => new Dictionary<string, string>();
    }

    private sealed class FakeSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;
        public bool Save() => true;
    }

    private sealed class FakeActivityLog : IActivityLog
    {
        public ObservableCollection<ActivityEntry> Entries { get; } = [];
        public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info) =>
            Entries.Insert(0, new ActivityEntry(message, kind, DateTimeOffset.UtcNow));

        public IReadOnlyList<string> Warnings => [.. Entries.Where(e => e.Kind == ActivityEntryKind.Warning).Select(e => e.Message)];
    }
}
