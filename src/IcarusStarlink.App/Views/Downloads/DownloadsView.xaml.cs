using System.Linq;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView()
    {
        InitializeComponent();
    }

    /// <summary>DataGrid.SelectedItems isn't bindable: hands the selected rows to Discard selected.</summary>
    private void DownloadsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DownloadsViewModel viewModel)
        {
            viewModel.SetSelectedPendingDownloads([.. DownloadsGrid.SelectedItems.OfType<PendingDownloadItemViewModel>()]);
        }
    }
}
