using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ExmodEditorWindow : Window
{
    public ExmodEditorWindow(ExmodEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>ListBox.SelectedItems isn't a bindable DependencyProperty in stock WPF — this is the one piece of multi-select mass edit that has to be pushed in from code-behind rather than bound directly.</summary>
    private void ItemsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ExmodEditorViewModel viewModel)
        {
            viewModel.SetSelectedItemsForMassEdit(ItemsListBox.SelectedItems.Cast<EditorItemViewModel>().ToList());
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
