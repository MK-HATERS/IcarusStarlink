using System.Collections.ObjectModel;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Only covers SelectDefaultPage's own first-run routing — everything else on MainViewModel
/// (theme switching, nav registration, the settings-saved CanExecute refresh) isn't exercised here.
/// </summary>
public class MainViewModelTests
{
    private static MainViewModel Build(string? icarusContentPath)
    {
        var settings = new AppSettings { IcarusContentPath = icarusContentPath };
        var vm = new MainViewModel(
            new FakeServiceProvider(), new FakeThemeService(), new FakeSettingsService(settings),
            new ActivityPanelViewModel(new FakeActivityLog()));
        return vm;
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
        vm.SelectedNavItem = vm.NavItems.First(item => item.Id == "nexus");

        vm.SelectDefaultPage();

        Assert.Equal("nexus", vm.SelectedNavItem?.Id);
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        // OnSelectedNavItemChanged resolves the page's real ViewModel via this — these tests only
        // care which NavItem.Id gets picked, not what CurrentPage actually resolves to, so any
        // non-null stand-in satisfies GetRequiredService's own null-check without needing a real
        // DI container or constructing LibraryViewModel/HelpViewModel's own dependencies.
        public object? GetService(Type serviceType) => new object();
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
