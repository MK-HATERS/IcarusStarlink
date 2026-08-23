using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>
/// "Search Original JSON" (classic IMM's own name) — a cross-file reference search over the whole
/// extracted game data: results are split into items NAMED like the term, and items whose JSON
/// VALUES reference it (e.g. every recipe consuming "Wood", every place a stat name is granted).
/// The per-row serialized index is built once by ExmodEditorViewModel (the whole data folder is
/// ~41MB of JSON, measured — comfortably an in-memory linear scan, no persistent index needed)
/// and handed in. Non-modal by design: this is a research companion used side-by-side with the
/// editor, same reasoning as the editor window itself being non-modal next to the main window.
/// </summary>
public partial class SearchGameDataWindow : Window
{
    private const int MaxResultsShown = 400;

    private readonly IReadOnlyList<GameDataSearchEntry> _entries;
    private readonly string _dataFolder;

    private sealed record ResultRow(string Display, string RealPath);

    public SearchGameDataWindow(IReadOnlyList<GameDataSearchEntry> entries, string dataFolder)
    {
        InitializeComponent();
        _entries = entries;
        _dataFolder = dataFolder;
        CountText.Text = $"{_entries.Count:N0} item(s) indexed — type to search.";
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var term = SearchBox.Text.Trim();
        if (term.Length < 2)
        {
            ResultsListBox.ItemsSource = null;
            CountText.Text = $"{_entries.Count:N0} item(s) indexed — type at least 2 characters.";
            return;
        }

        var named = new List<ResultRow>();
        var referencing = new List<ResultRow>();
        foreach (var entry in _entries)
        {
            if (entry.ItemName.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                named.Add(new ResultRow($"named   {entry.RealPath} — {entry.ItemName}", entry.RealPath));
            }
            else if (entry.RowJson.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                referencing.Add(new ResultRow($"ref     {entry.RealPath} — {entry.ItemName}", entry.RealPath));
            }
        }

        var combined = named.Concat(referencing).Take(MaxResultsShown).ToList();
        ResultsListBox.ItemsSource = combined;
        ResultsListBox.DisplayMemberPath = nameof(ResultRow.Display);

        var capNote = named.Count + referencing.Count > MaxResultsShown ? $" (showing first {MaxResultsShown})" : "";
        CountText.Text = $"{named.Count:N0} named '{term}' · {referencing.Count:N0} referencing it in their values{capNote}.";
    }

    private void ResultsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsListBox.SelectedItem is not ResultRow row)
        {
            return;
        }

        var realPath = Path.Combine(_dataFolder, row.RealPath.Replace('/', Path.DirectorySeparatorChar));
        UrlOpener.TryOpen(realPath);
    }
}
