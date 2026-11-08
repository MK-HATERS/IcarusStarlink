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

    /// <summary>The search runs once the typing stops, off the UI thread: it reads every row's JSON (~43,000 rows).</summary>
    private readonly DebounceTimer _searchDebounce;

    /// <summary>Moves on with each search started: only the newest term's results are shown.</summary>
    private int _searchVersion;

    private sealed record ResultRow(string Display, string RealPath);

    private sealed record SearchResult(List<ResultRow> Shown, int Named, int Referencing);

    public SearchGameDataWindow(IReadOnlyList<GameDataSearchEntry> entries, string dataFolder)
    {
        InitializeComponent();
        _entries = entries;
        _dataFolder = dataFolder;
        _searchDebounce = new DebounceTimer(TimeSpan.FromMilliseconds(250), Search);
        CountText.Text = $"{_entries.Count:N0} item(s) indexed — type to search.";
        Loaded += (_, _) => SearchBox.Focus();
        Closed += (_, _) =>
        {
            _searchDebounce.Cancel();
            _searchVersion++; // a search still running finds the window closed and shows nothing
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDebounce.Restart();
    }

    private async void Search()
    {
        var version = ++_searchVersion;
        var term = SearchBox.Text.Trim();
        if (term.Length < 2)
        {
            ResultsListBox.ItemsSource = null;
            CountText.Text = $"{_entries.Count:N0} item(s) indexed — type at least 2 characters.";
            return;
        }

        var entries = _entries;
        var result = await Task.Run(() => Find(entries, term));
        if (version != _searchVersion)
        {
            return; // the term changed while this one ran
        }

        ResultsListBox.ItemsSource = result.Shown;
        ResultsListBox.DisplayMemberPath = nameof(ResultRow.Display);

        var capNote = result.Named + result.Referencing > MaxResultsShown ? $" (showing first {MaxResultsShown})" : "";
        CountText.Text = $"{result.Named:N0} named '{term}' · {result.Referencing:N0} referencing it in their values{capNote}.";
    }

    /// <summary>Rows named like the term first, then rows whose values mention it; at most MaxResultsShown kept.</summary>
    private static SearchResult Find(IReadOnlyList<GameDataSearchEntry> entries, string term)
    {
        var named = new List<ResultRow>();
        var referencing = new List<ResultRow>();
        foreach (var entry in entries)
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

        return new SearchResult([.. named.Concat(referencing).Take(MaxResultsShown)], named.Count, referencing.Count);
    }

    /// <summary>
    /// Opens the result's real base-game file in the app's own read-only viewer — same reasoning as
    /// ExmodEditorViewModel.OpenOriginalFile's own doc comment: a user double-clicking a search
    /// result here is almost always about to go back and forth with this research window and/or the
    /// editor, so keeping it in-app beats shelling out to whatever the OS has associated with .json
    /// (previously this called UrlOpener.TryOpen, which — for a file rather than a directory — did
    /// exactly that).
    /// </summary>
    private void ResultsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row opens it (not one on the scrollbar or empty space).
        if (!ListDoubleClick.IsOnItem(ResultsListBox, e) || ResultsListBox.SelectedItem is not ResultRow row)
        {
            return;
        }

        var realPath = Path.Combine(_dataFolder, row.RealPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(realPath))
        {
            CountText.Text = $"No matching file at '{realPath}'.";
            return;
        }

        try
        {
            new TextFileViewerWindow(row.RealPath, realPath) { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File.Exists above only proves the file existed a moment ago and is enough to prove
            // *presence* — it can still return true for a file the current process can't actually
            // read the CONTENT of (an EFS-encrypted file, a narrow ACL granting attribute-read but
            // not data-read, or a plain TOCTOU race), which throws UnauthorizedAccessException from
            // TextFileViewerWindow's own File.ReadAllText, not IOException. Catching only IOException
            // let that propagate to the app's global unhandled-exception handler, which deliberately
            // terminates the app (App.xaml.cs's own doc comment: "report it and let WPF's own
            // default proceed") — turning a benign "can't preview this one file" into a crash that
            // could take unsaved editor work with it, in this non-modal side-by-side window.
            CountText.Text = $"Couldn't open the file: {ex.Message}";
        }
    }
}
