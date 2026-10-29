using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class CatalogEntryDetailWindow : Window
{
    /// <summary>Reached by the window's own XAML via RelativeSource AncestorType=Window, so the Download &amp; extract/Open readme buttons can invoke Downloads' page-level commands even though DataContext here is the catalog row itself, not the page.</summary>
    public DownloadsViewModel Downloads { get; }

    public CatalogEntryDetailWindow(CatalogEntryViewModel item, DownloadsViewModel downloads)
    {
        Downloads = downloads;
        InitializeComponent();
        DataContext = item;
    }
}
