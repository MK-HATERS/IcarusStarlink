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

    private sealed class FakeThemeService : IThemeService
    {
        public IReadOnlyList<string> AvailableThemes => [];
        public void ApplyTheme(string themeName)
        {
        }
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
