using System;
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
        Loaded += (_, _) =>
        {
            ArrangePanels();
            HookColumnWidthUpdates();
        };
        PanelsGrid.SizeChanged += (_, _) => ArrangePanels();
    }

    // What a row loses to the TreeViewItem template before its own Grid starts: the 19px expander
    // column, the 2px padding either side (Themes/Styles.xaml), plus a few px so rounding never
    // tips the row into a horizontal scroll.
    private const double RowChromeWidth = 19 + 4 + 14;
    private const double MinNameColumnWidth = 140;

    /// <summary>Below this width the two panels sit one above the other instead of side by side.</summary>
    private const double StackBelowWidth = 1180;

    private ScrollViewer? _dataTreeScrollViewer;
    private ScrollViewer? _ue4ssTreeScrollViewer;

    /// <summary>
    /// A shared-size column can't be star-sized (see the panels' own comment), so each panel's widths
    /// are set here from the room it has: Name grows up to a cap, and whatever is left goes to the
    /// empty column before the row buttons — the other columns stay next to the name instead of
    /// being pushed across a wide window (user report 2026-10-23: a 1,200px gap after every name).
    /// </summary>
    private bool _columnWidthsHooked;

    /// <summary>
    /// The widths depend only on each list's size and what its rows need (its scroll extent and
    /// viewport — a scrollbar appearing, a wider author, a column switched on). LayoutUpdated was
    /// used before, which fires on every layout pass anywhere in the app.
    /// </summary>
    private void HookColumnWidthUpdates()
    {
        if (!_columnWidthsHooked)
        {
            _columnWidthsHooked = true;
            foreach (var tree in new[] { LibraryTree, Ue4ssTree })
            {
                tree.SizeChanged += (_, _) => UpdateColumnWidths();
                tree.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
                {
                    if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0)
                    {
                        UpdateColumnWidths();
                    }
                }));
            }
        }

        UpdateColumnWidths();
    }

    private void UpdateColumnWidths()
    {
        _dataTreeScrollViewer ??= FindDescendant<ScrollViewer>(LibraryTree);
        _ue4ssTreeScrollViewer ??= FindDescendant<ScrollViewer>(Ue4ssTree);
        UpdateColumnWidths(_dataTreeScrollViewer, DataHeaderGrid, fillIndex: 11, maxName: 380, "LibraryNameColumnWidth", "LibraryFillWidth");
        UpdateColumnWidths(_ue4ssTreeScrollViewer, Ue4ssHeaderGrid, fillIndex: 8, maxName: 300, "Ue4ssNameColumnWidth", "Ue4ssFillWidth");
    }

    private void UpdateColumnWidths(ScrollViewer? scrollViewer, Grid headerGrid, int fillIndex, double maxName, string nameKey, string fillKey)
    {
        var visibleWidth = scrollViewer?.ViewportWidth ?? 0;
        if (visibleWidth <= 0 || headerGrid.ColumnDefinitions.Count <= fillIndex)
        {
            return;
        }

        const int nameIndex = 3;
        var otherColumns = headerGrid.ColumnDefinitions.Where((_, index) => index != nameIndex && index != fillIndex).Sum(c => c.ActualWidth);
        var room = Math.Floor(visibleWidth - RowChromeWidth - otherColumns);
        var name = Math.Clamp(room, MinNameColumnWidth, maxName);
        SetWidth(nameKey, name);
        SetWidth(fillKey, Math.Max(0, room - name));
    }

    private void SetWidth(string key, double width)
    {
        if (Resources[key] is double current && Math.Abs(current - width) < 1)
        {
            return;
        }

        Resources[key] = width;
    }

    private bool _isStacked;

    /// <summary>
    /// Side by side when there's room, one above the other (data on top) in a narrow window. The
    /// splitter keeps the data panel's share either way (AppSettings.LibraryPanelSplit).
    /// </summary>
    private void ArrangePanels()
    {
        var split = Math.Clamp(App.Settings?.Current.LibraryPanelSplit ?? 0.62, 0.2, 0.85);
        var stacked = PanelsGrid.ActualWidth > 0 && PanelsGrid.ActualWidth < StackBelowWidth;
        if (stacked)
        {
            DataColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(0);
            Ue4ssColumn.Width = new GridLength(0);
            DataColumn.MinWidth = 0;
            Ue4ssColumn.MinWidth = 0;
            DataRow.Height = new GridLength(split, GridUnitType.Star);
            SplitterRow.Height = new GridLength(14);
            Ue4ssRow.Height = new GridLength(1 - split, GridUnitType.Star);
            DataRow.MinHeight = 160;
            Ue4ssRow.MinHeight = 140;
            Place(PanelSplitter, row: 1, column: 0);
            Place(Ue4ssPanel, row: 2, column: 0);
            PanelSplitter.ResizeDirection = GridResizeDirection.Rows;
            PanelSplitter.Width = double.NaN;
            PanelSplitter.Height = 14;
        }
        else
        {
            DataRow.Height = new GridLength(1, GridUnitType.Star);
            SplitterRow.Height = new GridLength(0);
            Ue4ssRow.Height = new GridLength(0);
            DataRow.MinHeight = 0;
            Ue4ssRow.MinHeight = 0;
            DataColumn.Width = new GridLength(split, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(14);
            Ue4ssColumn.Width = new GridLength(1 - split, GridUnitType.Star);
            DataColumn.MinWidth = 320;
            Ue4ssColumn.MinWidth = 280;
            Place(PanelSplitter, row: 0, column: 1);
            Place(Ue4ssPanel, row: 0, column: 2);
            PanelSplitter.ResizeDirection = GridResizeDirection.Columns;
            PanelSplitter.Width = 14;
            PanelSplitter.Height = double.NaN;
        }

        _isStacked = stacked;
    }

    private static void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    /// <summary>Remembers where the splitter was left.</summary>
    private void PanelSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var (first, second) = _isStacked ? (DataRow.ActualHeight, Ue4ssRow.ActualHeight) : (DataColumn.ActualWidth, Ue4ssColumn.ActualWidth);
        if (first + second <= 0 || App.Settings is not { } settings)
        {
            return;
        }

        settings.Current.LibraryPanelSplit = Math.Round(first / (first + second), 3);
        settings.Save();
        ArrangePanels();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
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
            ClearTreeViewSelection(Ue4ssTree);
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
            // One selection across both panels: picking a row in one clears the other's, or a row
            // left selected there wouldn't fire again when clicked, and Enter/F2 acted on the other
            // panel's mod (review, 2026-10-23).
            if (e.NewValue is not null)
            {
                ClearTreeViewSelection(ReferenceEquals(sender, Ue4ssTree) ? LibraryTree : Ue4ssTree);
            }

            viewModel.SelectedItem = e.NewValue as LibraryItemViewModel;
        }
    }

    /// <summary>
    /// Double-clicking a mod row opens it in its own pop-out window (ModDetailWindow) — non-modal,
    /// so multiple can be open at once, mirroring the EXMOD editor's own per-mod window shape.
    /// SelectedItemChanged always fires first (same ordering MergeInstallView's own double-click
    /// handler relies on), so LibraryViewModel.SelectedItem already reflects whatever was actually
    /// clicked — a real mod row, or null for a group header/empty space, which this simply ignores.
    /// The click must land on that row itself: MouseDoubleClick fires for the whole tree, so a
    /// double-click on the scrollbar, or a quick double tick of a row's ON box or one of its
    /// buttons, used to pop open whichever mod was selected.
    /// </summary>
    private void LibraryTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (IsWithinButton(source)
            || FindTreeViewItemAncestor(source) is not TreeViewItem { DataContext: LibraryItemViewModel clicked })
        {
            return;
        }

        if (DataContext is LibraryViewModel { SelectedItem: { } item } viewModel && ReferenceEquals(item, clicked))
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

    /// <summary>True if start is (or sits inside) a CheckBox before reaching the row's own TreeViewItem boundary — lets LibraryTree_PreviewMouseLeftButtonDown step aside for the row's own bulk-select checkbox instead of also running its plain/Ctrl/Shift-click logic on the same physical click.</summary>
    private static bool IsWithinCheckBox(DependencyObject? start)
    {
        var element = start;
        while (element is not null and not TreeViewItem)
        {
            if (element is CheckBox)
            {
                return true;
            }

            element = GetParentAny(element);
        }

        return false;
    }

    /// <summary>True if start is (or sits inside) a button or checkbox of the row — clicking those runs their own action.</summary>
    private static bool IsWithinButton(DependencyObject? start)
    {
        var element = start;
        while (element is not null and not TreeViewItem)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            element = GetParentAny(element);
        }

        return false;
    }

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
        if (treeViewItem.DataContext is LibraryItemViewModel or Ue4ssModRowViewModel && DataContext is LibraryViewModel viewModel)
        {
            viewModel.EnsureBulkSelected(treeViewItem.DataContext);
        }
    }

    /// <summary>
    /// Ctrl-click toggles a row's own bulk-selection membership (for "Add to merge queue");
    /// Shift-click selects the whole range from the last plain/Ctrl-clicked row to this one; a
    /// plain click clears whatever was previously bulk-selected, so a fresh click always starts a
    /// new selection rather than silently adding to a stale one. Deliberately doesn't set
    /// e.Handled — TreeView's own normal single-select (SelectedItemChanged → SelectedItem →
    /// EnsureDetailsLoaded) still runs for whatever was actually clicked, any modifier held or not;
    /// the only side effect is the detail pane tracking the last-clicked row even during a
    /// multi-select, which is harmless.
    /// </summary>
    private void LibraryTree_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel)
        {
            return;
        }

        // The row's own bulk-select checkbox handles its click entirely through its own
        // Checked/Unchecked events below — without this, this same physical click would ALSO run
        // the plain-click-clears-selection branch further down (since no modifier is typically
        // held while clicking a checkbox), fighting the checkbox's own state right after it sets it.
        if (IsWithinCheckBox(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var element = FindTreeViewItemAncestor(e.OriginalSource as DependencyObject);

        var isCtrlHeld = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        var isShiftHeld = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);

        if (element is not TreeViewItem { DataContext: (LibraryItemViewModel or Ue4ssModRowViewModel) and var item })
        {
            if (!isCtrlHeld && !isShiftHeld)
            {
                viewModel.ClearBulkSelection();
            }

            return;
        }

        if (isShiftHeld)
        {
            viewModel.SelectBulkRange(item);
        }
        else if (isCtrlHeld)
        {
            viewModel.ToggleBulkSelection(item);
        }
        else
        {
            viewModel.SetBulkSelectionAnchor(item);
        }
    }

    /// <summary>
    /// A row's ON checkbox, clicked by the user (Click, not Checked: it never fires for binding
    /// updates, so applying it to the selection can't echo). The binding has already set this
    /// row; when the row is part of a multi-selection, every other selected row follows.
    /// </summary>
    private void EnabledCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: { } row } checkBox && DataContext is LibraryViewModel viewModel)
        {
            viewModel.ApplyEnabledToSelection(row, checkBox.IsChecked == true);
        }
    }

    /// <summary>Hovering a row lights up its other half in the other panel (MkHaters Bags ↔ MkHatersBags).</summary>
    private void Row_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => SetPartnerHighlight(sender, true);

    private void Row_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => SetPartnerHighlight(sender, false);

    private static void SetPartnerHighlight(object sender, bool on)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case LibraryItemViewModel { LinkedPartner: { } lua }:
                lua.IsLinkHighlighted = on;
                break;
            case Ue4ssModRowViewModel { LinkedPartner: { } item }:
                item.IsLinkHighlighted = on;
                break;
        }
    }

    /// <summary>A row's ⋯ button: the row's own right-click menu, with the row selected the way a right-click selects it.</summary>
    private void RowMoreButton_Click(object sender, RoutedEventArgs e)
    {
        var element = sender as DependencyObject;
        while (element is not null and not Grid { ContextMenu: not null })
        {
            element = GetParentAny(element);
        }

        if (element is not Grid { ContextMenu: { } menu } row)
        {
            return;
        }

        if (DataContext is LibraryViewModel viewModel && row.DataContext is LibraryItemViewModel or Ue4ssModRowViewModel)
        {
            viewModel.EnsureBulkSelected(row.DataContext);
        }

        // PlacementTarget must be the row: the menu's commands reach the page through its Tag.
        menu.PlacementTarget = row;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Keys in the Library: Space turns the selected mods on/off, Delete deletes them, Enter opens the
    /// selected mod's details, F2 renames it, Ctrl+A selects every mod shown, Ctrl+F jumps to search,
    /// Esc clears the selection. Typing in a box (search, notes) keeps its own keys.
    /// </summary>
    private void LibraryPage_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel)
        {
            return;
        }

        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        var ctrl = modifiers == System.Windows.Input.ModifierKeys.Control;
        if (ctrl && e.Key == System.Windows.Input.Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.ButtonBase or ComboBox)
        {
            return;
        }

        switch (e.Key)
        {
            case System.Windows.Input.Key.A when ctrl:
                viewModel.SelectAllForBulkCommand.Execute(Ue4ssTree.IsKeyboardFocusWithin ? "ue4ss" : "data");
                break;
            case System.Windows.Input.Key.Space when modifiers == System.Windows.Input.ModifierKeys.None:
                viewModel.ToggleSelectedEnabled();
                break;
            case System.Windows.Input.Key.Escape:
                viewModel.ClearBulkSelection();
                break;
            case System.Windows.Input.Key.Delete when viewModel.HasDataSelection:
                viewModel.DeleteItemCommand.Execute(null);
                break;
            case System.Windows.Input.Key.Delete when viewModel.BulkSelectedUe4ssRows.Count == 1:
                viewModel.DeleteUe4ssModCommand.Execute(viewModel.BulkSelectedUe4ssRows[0]);
                break;
            case System.Windows.Input.Key.Enter when viewModel.SelectedItem is { } item:
                new ModDetailWindow(item, viewModel) { Owner = Window.GetWindow(this) }.Show();
                break;
            case System.Windows.Input.Key.F2 when viewModel.SelectedItem is { } item:
                viewModel.RenameItemCommand.Execute(item);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Opens a toolbar button's own attached ContextMenu as a dropdown on a plain LEFT click —
    /// WPF only opens ContextMenu on right-click by default, so grouping several related actions
    /// behind one "Import ▾"-style button needs this one line of code-behind wherever it's used.
    /// </summary>
    private void DropdownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>Sets the drop cursor — without this, WPF shows the "no drop" cursor over the whole page even for a real file drag, since AllowDrop alone doesn't imply any particular drop is actually acceptable.</summary>
    private void LibraryPage_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Drag-and-drop counterpart to the "Import…" dialogs — a dropped folder, archive, or bare
    /// .pak routes through the exact same LibraryViewModel.ImportPaths auto-detection, so anything
    /// pickable in a file dialog can also just be dragged onto the page. Explorer's own drag
    /// already hands over every dropped item in one DragEventArgs, so dropping several at once (a
    /// mix of folders and archives included) works with no extra handling here.
    /// </summary>
    private async void LibraryPage_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            // async void: anything ImportPaths doesn't catch itself (its reload afterwards) would
            // otherwise take the app down from a drag-and-drop.
            try
            {
                await viewModel.ImportPaths(paths);
            }
            catch (Exception ex)
            {
                viewModel.StatusMessage = $"Import failed: {ex.Message}";
            }
        }
    }
}
