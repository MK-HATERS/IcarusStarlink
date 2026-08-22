using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>
    /// Replaces the separate "Add to queue" button — double-clicking a mod queues it directly.
    /// SelectedItemChanged (above) always fires first and already narrows SelectedLibraryItem to
    /// null for anything that isn't a real LibraryEntry (a LibraryGroup header, empty space), so
    /// this can reuse AddToQueueCommand as-is rather than re-deriving what was actually clicked.
    /// </summary>
    private void LibraryTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MergeInstallViewModel { SelectedLibraryItem: not null } viewModel)
        {
            viewModel.AddToQueueCommand.Execute(null);
        }
    }
}
