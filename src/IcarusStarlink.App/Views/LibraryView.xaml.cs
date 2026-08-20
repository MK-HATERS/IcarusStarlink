using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    // TreeView.SelectedItem has no setter, so it can't be bound directly — forward it to the
    // ViewModel here instead. Selecting a family header (not a specific variant) clears the
    // selection, matching "expand the family, pick one child."
    private void LibraryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is LibraryViewModel viewModel)
        {
            viewModel.SelectedItem = e.NewValue as LibraryItemViewModel;
        }
    }
}
