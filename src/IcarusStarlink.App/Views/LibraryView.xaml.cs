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

    // TreeView doesn't select an item on right-click the way ListBox does, so a context menu's
    // Pin/Favorite bindings (which target the right-clicked row's own DataContext regardless)
    // would apply to the correct mod but leave the tree's own highlight and the detail pane
    // pointing at whatever was selected before — right-clicking a mod should feel like clicking
    // it. Walking up from the raw hit-test source to the nearest TreeViewItem and selecting it
    // is the standard WPF workaround for TreeView's missing select-on-right-click behavior.
    private void LibraryTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        if (element is TreeViewItem item)
        {
            item.IsSelected = true;
        }
    }
}
