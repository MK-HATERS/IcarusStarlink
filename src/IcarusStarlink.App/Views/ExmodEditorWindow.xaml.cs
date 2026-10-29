using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ExmodEditorWindow : Window
{
    public ExmodEditorWindow(ExmodEditorViewModel viewModel)
    {
        InitializeComponent();
        Utilities.WindowSizeMemory.Remember(this, "mod-editor");
        DataContext = viewModel;

        // The list is grouped by the part of the game each row changes ("Armour", "Recipes").
        CollectionViewSource.GetDefaultView(viewModel.Items).GroupDescriptions.Add(new PropertyGroupDescription(nameof(EditorItemViewModel.Group)));
        GroupFields(viewModel);

        // See ScrollSyncHelper's own doc comment — keeps each view's scroll position in step with
        // whatever else has the same view open (a popped-out pane fixed to the same mode).
        ScrollSyncHelper.Attach(ItemFieldsScrollViewer, ItemFieldsScrollViewer.ScrollToVerticalOffset,
            viewModel, nameof(ExmodEditorViewModel.ItemFieldsScrollOffset), () => viewModel.ItemFieldsScrollOffset, v => viewModel.ItemFieldsScrollOffset = v, this);

        // Unsaved changes ask Save / Don't save / Cancel instead of vanishing with the window.
        Closing += (_, e) => e.Cancel = !viewModel.ConfirmClose();
        Closed += (_, _) => viewModel.Detach();
    }

    /// <summary>
    /// The Fields view's sections ("Numbers", "Names and text"…), by EditorFieldViewModel.Group;
    /// the view model already lists them section by section. The default view is shared with a
    /// popped-out pane, so the grouping is added once.
    /// </summary>
    internal static void GroupFields(ExmodEditorViewModel viewModel)
    {
        var view = CollectionViewSource.GetDefaultView(viewModel.Fields);
        if (view.GroupDescriptions.Count == 0)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EditorFieldViewModel.Group)));
        }
    }

    /// <summary>ListBox.SelectedItems isn't a bindable DependencyProperty in stock WPF — this is the one piece of multi-select mass edit that has to be pushed in from code-behind rather than bound directly.</summary>
    private void ItemsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ExmodEditorViewModel viewModel)
        {
            viewModel.SetSelectedItemsForMassEdit(ItemsListBox.SelectedItems.Cast<EditorItemViewModel>().ToList());
        }
    }

    /// <summary>More ▾ opens its menu on a plain click (a context menu otherwise needs a right-click).</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = MoreButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>Ctrl+F focuses the filter box — a pure-UI focus concern the ViewModel has no clean way to reach.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
