using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Database page: everywhere mods can be found, in one place — Nexus and the IMM Database as
/// two tabs — so Library only holds what you have. Pages hand off here with
/// NavigateToPageMessage("nexus") / ("imm-database"), which pick the tab.
/// </summary>
public sealed partial class DatabaseViewModel : ObservableObject
{
    public const int NexusTab = 0;
    public const int ImmDatabaseTab = 1;

    public DatabaseViewModel(NexusCatalogViewModel nexus, LibraryViewModel library)
    {
        Nexus = nexus;
        Library = library;
        WeakReferenceMessenger.Default.Register<NavigateToPageMessage>(this, (recipient, message) =>
        {
            var page = (DatabaseViewModel)recipient;
            if (message.NavItemId == "nexus")
            {
                page.SelectedTab = NexusTab;
            }
            else if (message.NavItemId == "imm-database")
            {
                page.SelectedTab = ImmDatabaseTab;
            }
        });
    }

    public string Title => "Database";

    public NexusCatalogViewModel Nexus { get; }

    /// <summary>The IMM Database tab's bindings live on Library's Downloads, where they always have.</summary>
    public LibraryViewModel Library { get; }

    [ObservableProperty]
    private int _selectedTab;
}
