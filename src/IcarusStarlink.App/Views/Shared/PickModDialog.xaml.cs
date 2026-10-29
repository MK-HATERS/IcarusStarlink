using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Views;

/// <summary>"Merge existing mod into this one" — lists every other EXMOD-based Library entry (an opaque pak has no Rows/FileItems to merge) to pick a source from.</summary>
public partial class PickModDialog : Window
{
    private readonly List<LibraryEntry> _allEntries;

    public LibraryEntry? SelectedEntry { get; private set; }

    public PickModDialog(IEnumerable<LibraryEntry> candidates)
    {
        InitializeComponent();

        _allEntries = [.. candidates.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        ModsListBox.ItemsSource = _allEntries;
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();
        ModsListBox.ItemsSource = string.IsNullOrEmpty(filter)
            ? _allEntries
            : _allEntries.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (ModsListBox.SelectedItem is not LibraryEntry selected)
        {
            return;
        }

        SelectedEntry = selected;
        DialogResult = true;
    }

    private void ModsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Merge_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
