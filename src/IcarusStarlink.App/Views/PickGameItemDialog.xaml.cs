using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>"Add item from game data" — picks one real DataTable row out of the whole extracted game data (classic IMM's own "Add Item to Mod", its changelog's most-iterated editor feature). The index is built once by ExmodEditorViewModel and handed in, so reopening the dialog costs nothing.</summary>
public partial class PickGameItemDialog : Window
{
    private readonly IReadOnlyList<GameDataItemRef> _allItems;

    public GameDataItemRef? SelectedItem { get; private set; }

    public PickGameItemDialog(IReadOnlyList<GameDataItemRef> items)
    {
        InitializeComponent();
        _allItems = items;
        ItemsListBox.ItemsSource = _allItems;
        UpdateCount(_allItems.Count);
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var filter = FilterBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? _allItems
            : [.. _allItems.Where(i => i.Display.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        ItemsListBox.ItemsSource = filtered;
        UpdateCount(filtered.Count);
    }

    private void UpdateCount(int count) => CountText.Text = $"{count:N0} item(s) across the extracted game data";

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsListBox.SelectedItem is not GameDataItemRef selected)
        {
            return;
        }

        SelectedItem = selected;
        DialogResult = true;
    }

    private void ItemsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Add_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
