using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView()
    {
        InitializeComponent();
    }

    /// <summary>DataGrid.SelectedItems isn't a bindable DependencyProperty — forwards the current multi-selection into the ViewModel for DownloadAndExtractSelectedCommand, same workaround the EXMOD editor's mass-edit selection already uses.</summary>
    private void CatalogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DownloadsViewModel viewModel)
        {
            viewModel.SetSelectedCatalogEntries([.. CatalogGrid.SelectedItems.Cast<CatalogEntryViewModel>()]);
        }
    }
}
