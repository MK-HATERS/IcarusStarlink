using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Covers SelectDefaultPage's own first-run routing and the page hand-offs (which Database tab a
/// "nexus" / "imm-database" hand-off lands on) — theme switching and the settings-saved
/// CanExecute refresh aren't exercised here.
/// </summary>
[Collection(MessengerCollection.Name)]
public class MainViewModelTests
{
    private static MainViewModel Build(string? icarusContentPath, IServiceProvider? services = null)
    {
        var settings = new AppSettings { IcarusContentPath = icarusContentPath };
        var vm = new MainViewModel(
            services ?? new FakeServiceProvider(), new FakeThemeService(), new FakeSettingsService(settings),
            new ActivityPanelViewModel(new FakeActivityLog()));
        return vm;
    }

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

    /// <summary>Ctrl+1 … Ctrl+9 (Ctrl+0 = the tenth) go to the menu's pages in order; a number past the last does nothing.</summary>
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
            Assert.False(vm.GoToPageNumber(vm.NavItems.Count + 1));
            Assert.Equal(vm.NavItems[2], vm.SelectedNavItem);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// The user's ask (2026-10-30): Play could be clicked again and again, asking Steam to start the
    /// game each time. A click shows Starting… and further clicks do nothing until the game shows up
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
            new ActivityPanelViewModel(new FakeActivityLog()), game, () => now, opened.Add, () => { focused++; return true; });
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
            new ActivityPanelViewModel(new FakeActivityLog()), game, () => now, opened.Add, () => true);
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
        public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info)
        {
        }
    }
}
