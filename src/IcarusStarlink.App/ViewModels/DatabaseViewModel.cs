using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Database page: everywhere mods can be found, in one place — Nexus and the IMM Database as
/// two tabs — so Library only holds what you have. Pages hand off here with
/// NavigateToPageMessage("nexus") / ("imm-database"), which pick the tab.
///
/// Also decides when the New tags are recorded as seen: a tab opened this session counts as
/// looked at, and RecordSeen (Starlink closing) marks that tab's mods seen. So the tags stay put
/// while you go back and forth between pages, and are gone next time. Each tab's Mark all seen
/// clears them at once.
/// </summary>
public sealed partial class DatabaseViewModel : ObservableObject
{
    public const int NexusTab = 0;
    public const int ImmDatabaseTab = 1;

    private bool _isShown;
    private bool _nexusLookedAt;
    private bool _immDatabaseLookedAt;

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

        // The tab headers carry the "(5 new)" counts. (Null only in hand-off tests that build the page bare.)
        if (nexus is not null)
        {
            nexus.PropertyChanged += OnTabCountChanged;
        }

        if (library?.Downloads is { } downloads)
        {
            downloads.PropertyChanged += OnTabCountChanged;
        }
    }

    /// <summary>Test seam: the IMM Database tab's VM straight, when there is no LibraryViewModel around it.</summary>
    internal static DatabaseViewModel ForTests(NexusCatalogViewModel nexus, DownloadsViewModel immDatabase)
    {
        var page = new DatabaseViewModel(nexus, null!) { _immDatabase = immDatabase };
        immDatabase.PropertyChanged += page.OnTabCountChanged;
        return page;
    }

    private DownloadsViewModel? _immDatabase;

    private DownloadsViewModel? ImmDatabase => _immDatabase ?? Library?.Downloads;

    public string Title => "Database";

    public NexusCatalogViewModel Nexus { get; }

    /// <summary>The IMM Database tab's bindings live on Library's Downloads, where they always have.</summary>
    public LibraryViewModel Library { get; }

    [ObservableProperty]
    private int _selectedTab;

    /// <summary>"Nexus (3 new)" while mods were published since your last visit, else "Nexus".</summary>
    public string NexusTabHeader => Nexus is { HasNew: true } ? $"Nexus ({Nexus.NewCountDisplay})" : "Nexus";

    /// <summary>"IMM Database (5 new)" while mods were added or updated since your last visit.</summary>
    public string ImmDatabaseTabHeader => ImmDatabase is { CatalogNewCount: > 0 } imm
        ? $"IMM Database ({imm.CatalogNewCount} new)"
        : "IMM Database";

    private void OnTabCountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NexusCatalogViewModel.NewCount) && ReferenceEquals(sender, Nexus))
        {
            OnPropertyChanged(nameof(NexusTabHeader));
        }
        else if (e.PropertyName is nameof(DownloadsViewModel.CatalogNewCount))
        {
            OnPropertyChanged(nameof(ImmDatabaseTabHeader));
        }
    }

    partial void OnSelectedTabChanged(int value) => NoteTabLookedAt();

    /// <summary>The shell shows this page (MainViewModel).</summary>
    public void OnPageShown()
    {
        _isShown = true;
        // Deferred a moment: "Find in Database" shows the page and THEN picks the IMM tab, and the
        // Nexus tab it passed through on the way was never really looked at.
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(NoteTabLookedAt, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            NoteTabLookedAt();
        }
    }

    /// <summary>The shell moved to another page.</summary>
    public void OnPageLeft() => _isShown = false;

    private void NoteTabLookedAt()
    {
        if (!_isShown)
        {
            return;
        }

        if (SelectedTab == NexusTab)
        {
            _nexusLookedAt = true;
        }
        else if (SelectedTab == ImmDatabaseTab)
        {
            _immDatabaseLookedAt = true;
        }
    }

    /// <summary>Starlink is closing: every tab opened this session has its mods recorded as seen. A tab never opened keeps its New tags for next time.</summary>
    public void RecordSeen()
    {
        if (_nexusLookedAt)
        {
            Nexus?.MarkSeen();
        }

        if (_immDatabaseLookedAt)
        {
            ImmDatabase?.MarkCatalogSeen();
        }
    }
}
