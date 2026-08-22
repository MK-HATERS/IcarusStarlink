using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>
/// "Add item from game data" — picks one real DataTable row out of the whole extracted game data
/// (classic IMM's own "Add Item to Mod", its changelog's most-iterated editor feature). The index
/// is built once by ExmodEditorViewModel and handed in, so reopening the dialog costs nothing.
/// The "hide items already in this mod" toggle is classic IMM's other companion workflow ("View
/// Originals with modded items hidden"): with it on, what remains visible in a file is exactly
/// what the mod hasn't accounted for — the fast way to spot rows a game patch added.
/// </summary>
public partial class PickGameItemDialog : Window
{
    private readonly IReadOnlyList<GameDataItemRef> _allItems;
    private readonly IReadOnlySet<string> _coveredKeys;

    public GameDataItemRef? SelectedItem { get; private set; }

    /// <param name="coveredKeys">Keys (see CoverageKey) of items the mod already touches — drives the hide toggle. Empty set disables nothing; the toggle just has no effect.</param>
    public PickGameItemDialog(IReadOnlyList<GameDataItemRef> items, IReadOnlySet<string> coveredKeys)
    {
        InitializeComponent();
        _allItems = items;
        _coveredKeys = coveredKeys;
        ApplyFilter();
        Loaded += (_, _) => FilterBox.Focus();
    }

    public static string CoverageKey(string currentFile, string itemName) => $"{currentFile}|{itemName}".ToLowerInvariant();

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void HideCoveredBox_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        var hideCovered = HideCoveredBox.IsChecked == true;

        IEnumerable<GameDataItemRef> query = _allItems;
        if (hideCovered)
        {
            query = query.Where(i => !_coveredKeys.Contains(CoverageKey(i.CurrentFile, i.ItemName)));
        }

        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(i => i.Display.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query.ToList();
        ItemsListBox.ItemsSource = filtered;
        CountText.Text = hideCovered
            ? $"{filtered.Count:N0} item(s) this mod doesn't touch yet"
            : $"{filtered.Count:N0} item(s) across the extracted game data";
    }

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
