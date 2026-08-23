using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
            {
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            }

            if (e.NewValue is INotifyPropertyChanged newVm)
            {
                newVm.PropertyChanged += ViewModel_PropertyChanged;
            }
        };
    }

    // TreeView.SelectedItem has no setter (see LibraryTree_SelectedItemChanged's own comment), so
    // setting the ViewModel's SelectedItem to null on its own never reaches the real
    // TreeViewItem.IsSelected — DeleteSelected() deletes the mod and removes its row while WPF
    // still considers that TreeViewItem "selected". WPF's TreeView does not defensively clean up
    // its own internal selection tracking when a still-selected container disappears via a
    // targeted Remove (as opposed to a full Reset) — it silently blocks the next selection
    // attempt (clicking a different, still-present row does nothing) until something else forces
    // the TreeView into a clean state, which is why navigating away and back (a full visual-tree
    // rebuild) was the only way to recover. This handler runs synchronously off the SelectedItem
    // PropertyChanged notification — before Reload()'s own removal of the row — so it can clear
    // the real IsSelected on the doomed container while it's still there to find.
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.SelectedItem)
            && DataContext is LibraryViewModel { SelectedItem: null })
        {
            ClearTreeViewSelection(LibraryTree);
        }
    }

    private static void ClearTreeViewSelection(ItemsControl container)
    {
        foreach (var item in container.Items)
        {
            if (container.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem treeViewItem)
            {
                continue;
            }

            if (treeViewItem.IsSelected)
            {
                treeViewItem.IsSelected = false;
            }

            ClearTreeViewSelection(treeViewItem);
        }
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

    /// <summary>
    /// Double-clicking a mod row opens it in its own pop-out window (ModDetailWindow) — non-modal,
    /// so multiple can be open at once, mirroring the EXMOD editor's own per-mod window shape.
    /// SelectedItemChanged always fires first (same ordering MergeInstallView's own double-click
    /// handler relies on), so LibraryViewModel.SelectedItem already reflects whatever was actually
    /// clicked — a real mod row, or null for a group header/empty space, which this simply ignores.
    /// </summary>
    private void LibraryTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is LibraryViewModel { SelectedItem: { } item } viewModel)
        {
            new ModDetailWindow(item, viewModel) { Owner = Window.GetWindow(this) }.Show();
        }
    }

    /// <summary>
    /// Walks up from a raw hit-test source to the nearest TreeViewItem. VisualTreeHelper.GetParent
    /// throws for anything that isn't a real Visual/Visual3D — a real crash found live:
    /// OriginalSource can be a System.Windows.Documents.Run (a ContentElement, not a Visual — WPF
    /// re-raises routed input events onto inline text elements like Run/Hyperlink so they can still
    /// participate, which is exactly what a MenuItem's own Run-based Header content produces).
    /// GetParentAny below only falls back to LogicalTreeHelper for that one non-Visual hop (Run → its
    /// containing TextBlock), then resumes the normal, already-tested VisualTreeHelper walk — safer
    /// than switching the whole walk to LogicalTreeHelper, which templated content doesn't always
    /// mirror exactly.
    /// </summary>
    private static DependencyObject? FindTreeViewItemAncestor(DependencyObject? start)
    {
        var element = start;
        while (element is not null and not TreeViewItem)
        {
            element = GetParentAny(element);
        }

        return element;
    }

    private static DependencyObject? GetParentAny(DependencyObject child) =>
        child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);

    // TreeView doesn't select an item on right-click the way ListBox does, so a context menu's
    // Pin/Favorite bindings (which target the right-clicked row's own DataContext regardless)
    // would apply to the correct mod but leave the tree's own highlight and the detail pane
    // pointing at whatever was selected before — right-clicking a mod should feel like clicking
    // it. Walking up from the raw hit-test source to the nearest TreeViewItem and selecting it
    // is the standard WPF workaround for TreeView's missing select-on-right-click behavior.
    //
    // Also ensures the right-clicked row is part of the bulk (Ctrl/Shift-click) selection before
    // the context menu's own "Add to merge queue" item evaluates PlacementTarget.Tag.BulkSelectedCount
    // — a row already part of a multi-selection stays part of it, one clicked fresh replaces it,
    // matching Explorer's own right-click convention.
    private void LibraryTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var element = FindTreeViewItemAncestor(e.OriginalSource as DependencyObject);

        if (element is not TreeViewItem treeViewItem)
        {
            return;
        }

        treeViewItem.IsSelected = true;
        if (treeViewItem.DataContext is LibraryItemViewModel item && DataContext is LibraryViewModel viewModel)
        {
            viewModel.EnsureBulkSelected(item);
        }
    }

    /// <summary>
    /// Ctrl-click toggles a row's own bulk-selection membership (for "Add to merge queue");
    /// a plain click clears whatever was previously bulk-selected, so a fresh click always starts a
    /// new selection rather than silently adding to a stale one. Deliberately doesn't set
    /// e.Handled — TreeView's own normal single-select (SelectedItemChanged → SelectedItem →
    /// EnsureDetailsLoaded) still runs for whatever was actually clicked, Ctrl held or not; the only
    /// side effect is the detail pane tracking the last-clicked row even during a multi-select,
    /// which is harmless.
    /// </summary>
    private void LibraryTree_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel)
        {
            return;
        }

        var element = FindTreeViewItemAncestor(e.OriginalSource as DependencyObject);

        var isCtrlHeld = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);

        if (element is not TreeViewItem { DataContext: LibraryItemViewModel item })
        {
            if (!isCtrlHeld)
            {
                viewModel.ClearBulkSelection();
            }

            return;
        }

        if (isCtrlHeld)
        {
            viewModel.ToggleBulkSelection(item);
        }
        else if (viewModel.BulkSelectedItems.Count > 0)
        {
            viewModel.ClearBulkSelection();
        }
    }

    /// <summary>DataGrid.SelectedItems isn't a bindable DependencyProperty — forwards the current multi-selection into DownloadsViewModel for DownloadAndExtractSelectedCommand, same workaround the EXMOD editor's mass-edit selection already uses. Moved here from the former standalone Downloads page along with the IMM Database tab itself.</summary>
    private void CatalogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel)
        {
            viewModel.Downloads.SetSelectedCatalogEntries([.. CatalogGrid.SelectedItems.Cast<CatalogEntryViewModel>()]);
        }
    }
}
