using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>The IMM Database tab of the Database page (moved from Library with its handlers).</summary>
public partial class ImmDatabaseView : UserControl
{
    public ImmDatabaseView()
    {
        InitializeComponent();
    }

    /// <summary>DataGrid.SelectedItems isn't a bindable DependencyProperty — forwards the current multi-selection into DownloadsViewModel for DownloadAndExtractSelectedCommand, same workaround the EXMOD editor's mass-edit selection already uses. Moved here from the former standalone Downloads page along with the IMM Database tab itself.</summary>
    private void CatalogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel)
        {
            viewModel.Downloads.SetSelectedCatalogEntries([.. CatalogGrid.SelectedItems.Cast<CatalogEntryViewModel>()]);
        }
    }

    /// <summary>
    /// Double-clicking a catalog row downloads it — the same action its own detail-pane "Download
    /// &amp; extract" button already runs, just reachable without moving to the side panel first.
    /// Added alongside the IsReadOnly fix on this grid: double-click used to (unintentionally) enter
    /// WPF's default cell-edit mode, which crashed the app outright — this replaces the crash with
    /// the useful action a double-click on a browsable row should actually have had all along,
    /// matching every other "pick one from a list" dialog in this app (PickNexusFileDialog,
    /// PickFileDialog, etc.), which all bind double-click straight to their own primary action.
    /// Only for a double-click ON a row: MouseDoubleClick fires for the whole grid, so double-clicking
    /// a column header's edge (to auto-size it) or the scrollbar used to download whatever row
    /// happened to be selected.
    /// </summary>
    private void CatalogGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!IsWithinRow(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (DataContext is LibraryViewModel viewModel && viewModel.Downloads.DownloadAndExtractCommand.CanExecute(null))
        {
            viewModel.Downloads.DownloadAndExtractCommand.Execute(null);
        }
    }

    /// <summary>True when the clicked element sits inside a DataGridRow (not a header, scrollbar or empty space).</summary>
    private static bool IsWithinRow(DependencyObject? start)
    {
        for (var element = start; element is not null; element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                 : LogicalTreeHelper.GetParent(element))
        {
            if (element is DataGridRow)
            {
                return true;
            }

            if (element is System.Windows.Controls.Primitives.DataGridColumnHeader or System.Windows.Controls.Primitives.ScrollBar)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Opens the currently-selected catalog row in its own pop-out — non-modal, same shape as Library's own ModDetailWindow, for the image/full description/catalog metadata the compact side panel has no room for.</summary>
    private void ShowCatalogEntryDetails_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel { Downloads.SelectedCatalogEntry: { } entry } viewModel)
        {
            new CatalogEntryDetailWindow(entry, viewModel.Downloads) { Owner = Window.GetWindow(this) }.Show();
        }
    }
}
