using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Views;

public partial class MergeInstallView : UserControl
{
    public MergeInstallView()
    {
        InitializeComponent();
    }

    // TreeView.SelectedItem has no setter, so it can't be bound directly — same forwarding
    // LibraryView already does. Selecting a family header (a LibraryGroup, not a specific
    // variant) clears the selection, matching "expand the family, pick one child to add."
    private void LibraryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MergeInstallViewModel viewModel)
        {
            viewModel.SelectedLibraryItem = e.NewValue as LibraryEntry;
        }
    }
}
