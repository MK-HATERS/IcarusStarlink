using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One entry in the history picker — a report plus the display text for it, so the ComboBox doesn't need its own converter just to show a date and a count.</summary>
public sealed record WeeklyChangeReportSummary(WeeklyChangeReport Report, string Display);

/// <summary>
/// A browsable history of WeeklyChangeReports (roughly a month's worth — see
/// IWeeklyChangeReportStore) rather than just the single latest one, mirroring classic IMM's own
/// "This Weeks Changes" feature (see WeeklyChangeReport's own doc comment) but as a dedicated nav
/// page instead of a synthetic library entry.
/// </summary>
public sealed partial class WeeklyChangesViewModel : ObservableObject
{
    private readonly IWeeklyChangeReportStore _store;
    private readonly IDialogService _dialogs;

    public string Title => "Weekly Changes";

    /// <summary>Settings' view model, so this page's Refresh game data button runs the same command
    /// as Settings' own (no second copy of the extraction). SettingsViewModel is a DI singleton built
    /// at app start, so this is never what first creates it.</summary>
    public SettingsViewModel Settings { get; }

    public ObservableCollection<WeeklyChangeReportSummary> ReportHistory { get; } = [];

    [ObservableProperty]
    private WeeklyChangeReportSummary? _selectedReport;

    public ObservableCollection<ChangedDataFileViewModel> ChangedFiles { get; } = [];

    [ObservableProperty]
    private ChangedDataFileViewModel? _selectedFile;

    [ObservableProperty]
    private string _summaryMessage = "";

    /// <summary>Every file in the report shown; ChangedFiles is the ones the search and the mods switch leave.</summary>
    private readonly List<ChangedDataFileViewModel> _allFiles = [];

    /// <summary>Reads the rows each Library mod changes (slow: run off the UI thread); null when there's no Library to read.</summary>
    private readonly Func<IReadOnlyList<ModRowTouch>>? _readMyModRows;

    /// <summary>Table key (LibraryModRows.TableKey) → row name → the mods that change it. Null until read, and again after the Library changes.</summary>
    private Dictionary<string, Dictionary<string, List<string>>>? _myModRows;

    /// <summary>Words to look for in the file names, item names and field names.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Shows only the changed rows that a mod in your Library also changes.</summary>
    [ObservableProperty]
    private bool _showOnlyMyModRows;

    /// <summary>"Reading your mods…" while the Library is read, or why it couldn't be; null otherwise.</summary>
    [ObservableProperty]
    private string? _myModRowsStatus;

    /// <summary>The shown file's rows that the search and the mods switch leave.</summary>
    [ObservableProperty]
    private IReadOnlyList<ChangedRowViewModel> _selectedFileRows = [];

    public bool CanShowOnlyMyModRows => _readMyModRows is not null;

    /// <summary>True while the mods switch has the Library read: the Your mods column has names to show.</summary>
    public bool HasMyModRows => ShowOnlyMyModRows && _myModRows is not null;

    /// <summary>The last Library read the mods switch started (tests wait on it).</summary>
    internal Task MyModRowsLoad { get; private set; } = Task.CompletedTask;

    /// <param name="messenger">Injectable like Merge &amp; Install's, so a test can use its own instead of the shared Default.</param>
    /// <param name="dialogService">Asks before Remove; the real themed dialogs when left out.</param>
    /// <param name="myModRows">The rows each Library mod changes (LibraryModRows.Read), for "Only rows my mods change"; the switch is off without it.</param>
    public WeeklyChangesViewModel(IWeeklyChangeReportStore store, SettingsViewModel settings, IMessenger? messenger = null, IDialogService? dialogService = null, Func<IReadOnlyList<ModRowTouch>>? myModRows = null)
    {
        _store = store;
        Settings = settings;
        _dialogs = dialogService ?? new WpfDialogService();
        _readMyModRows = myModRows;
        messenger ??= WeakReferenceMessenger.Default;

        // A mod added, changed or removed: the rows read before are out of date.
        messenger.Register<LibraryChangedMessage>(this, (recipient, _) => ((WeeklyChangesViewModel)recipient).OnLibraryChanged());

        // This VM is a DI singleton, constructed once on first navigation here — without this, a
        // report saved later (from Settings' Refresh game data, or this page's own shortcut)
        // wouldn't show up until some unrelated trigger happened to rebuild this page. Same
        // rationale as LibraryViewModel's own LibraryChangedMessage registration.
        messenger.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((WeeklyChangesViewModel)recipient).ReloadHistory());

        ReloadHistory();
    }

    partial void OnSelectedReportChanged(WeeklyChangeReportSummary? value)
    {
        ShowReport(value?.Report);
        RemoveReportCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveReport() => SelectedReport is not null;

    /// <summary>
    /// Weekly Changes › Remove: deletes the report shown (an empty one from an extra refresh, say)
    /// after the user says yes. Only Starlink's own record goes; the game's data and the Data
    /// folder are not touched, and the next refresh still compares with the last extraction. The
    /// old-copy check loses that report's old values, which is why a report with changes warns.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveReport))]
    private void RemoveReport()
    {
        if (SelectedReport is not { } shown)
        {
            return;
        }

        var report = shown.Report;
        // A report's old values also feed the old-copy check (GameHistory in AppServices), so
        // removing one with changes says what that check loses.
        var hasChanges = report.ChangedFiles.Count > 0;
        var message = $"Remove the report from {report.CurrentUpdateAt.LocalDateTime:d MMM yyyy, HH:mm} ({FilesChangedText(report.ChangedFiles.Count)})?\n\n"
            + "It goes from the History list for good. The game's files and your mods aren't touched."
            + (hasChanges ? "\n\nStarlink also uses it to spot mods that still have this update's old values; that check won't know them any more." : "");
        if (!_dialogs.Confirm(message, "Remove report", hasChanges ? ThemedConfirmSeverity.Warning : ThemedConfirmSeverity.Question))
        {
            return;
        }

        if (!_store.Remove(report))
        {
            SummaryMessage = "Couldn't remove that report: its file may be open in another program or read-only. Try again in a moment.";
            return;
        }

        ReloadHistory();
    }

    /// <summary>
    /// Always lands on the newest report (History is newest first). This only re-runs because a
    /// refresh just saved a new report, which is the one the user wants to see — re-selecting the
    /// report picked before kept showing the old one as if nothing had changed.
    /// </summary>
    private void ReloadHistory()
    {
        ReportHistory.Clear();
        foreach (var report in _store.History)
        {
            ReportHistory.Add(new WeeklyChangeReportSummary(report, DisplayFor(report)));
        }

        // Re-selecting (rather than just calling ShowReport directly) keeps SelectedReport and the
        // ComboBox's own binding in sync — a plain ShowReport call here would leave the ComboBox
        // showing the previous selection's text even though the underlying list just changed.
        SelectedReport = ReportHistory.FirstOrDefault();

        if (ReportHistory.Count == 0)
        {
            ShowReport(null);
        }
    }

    /// <summary>"22 Oct 2026, 15:08 · 5 files changed" — the history picker's line for one report.</summary>
    internal static string DisplayFor(WeeklyChangeReport report) =>
        $"{report.CurrentUpdateAt.LocalDateTime:d MMM yyyy, HH:mm} · {FilesChangedText(report.ChangedFiles.Count)}"
        + (BuildText(report) is { } build ? $" · {build}" : "");

    /// <summary>"game build 25956278" for a report made from a Steam build; null for one saved before
    /// builds were kept, or a copy without Steam's record (data.pak's hash means nothing to a player).</summary>
    private static string? BuildText(WeeklyChangeReport report) =>
        report.GameBuildId is { Length: > 0 } build && !GameUpdateDetector.IsHashBuild(build) ? $"game build {build}" : null;

    /// <summary>"no changes", "1 file changed", "5 files changed".</summary>
    internal static string FilesChangedText(int count) => count switch
    {
        0 => "no changes",
        1 => "1 file changed",
        _ => $"{count} files changed",
    };

    /// <summary>What the file list says when it's empty; null while it has files.</summary>
    public string? FilesEmptyText => ChangedFiles.Count > 0
        ? null
        : SelectedReport is null
            ? "Nothing to compare yet."
            : _allFiles.Count == 0
                ? "No files changed in this update."
                : HasMyModRows
                    ? IsSearching ? "Nothing your mods change matches the search." : "None of your mods change the rows this update changed."
                    : "No changed files match the search.";

    /// <summary>What the changes table says when it has no rows; null while it has some.</summary>
    public string? RowsEmptyText => SelectedReport is null
        ? "Refresh game data now and again after the next game update; what changed shows here."
        : _allFiles.Count == 0
            ? "No changes in this update."
            : SelectedFile is null
                ? ChangedFiles.Count == 0 ? "Clear the search or the mods switch to see every change." : "Pick a file on the left."
                : SelectedFile.Rows.Count == 0
                    ? $"{SelectedFile.StatusLabel}: no row-by-row changes to show."
                    : SelectedFileRows.Count == 0
                        ? "No rows in this file match."
                        : null;

    partial void OnSelectedFileChanged(ChangedDataFileViewModel? value) => RefreshSelectedFileRows();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowOnlyMyModRowsChanged(bool value)
    {
        if (value && _myModRows is null && _readMyModRows is not null)
        {
            MyModRowsLoad = LoadMyModRowsAsync(_readMyModRows);
            return;
        }

        ApplyFilter();
    }

    private bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Reads the Library's rows off the UI thread, then filters. The list keeps showing every file meanwhile.</summary>
    private async Task LoadMyModRowsAsync(Func<IReadOnlyList<ModRowTouch>> read)
    {
        MyModRowsStatus = "Reading your mods…";
        try
        {
            var touches = await Task.Run(read);
            _myModRows = IndexByTable(touches);
            MyModRowsStatus = null;
        }
        catch (Exception ex)
        {
            _myModRows = null;
            MyModRowsStatus = $"Couldn't read the mods in your Library ({ex.Message}). Try again in a moment.";
            ShowOnlyMyModRows = false;
            return;
        }

        ApplyFilter();
    }

    private void OnLibraryChanged()
    {
        _myModRows = null;
        if (ShowOnlyMyModRows && _readMyModRows is not null)
        {
            MyModRowsLoad = LoadMyModRowsAsync(_readMyModRows);
        }
    }

    private static Dictionary<string, Dictionary<string, List<string>>> IndexByTable(IEnumerable<ModRowTouch> touches)
    {
        var index = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var touch in touches)
        {
            var key = LibraryModRows.TableKey(touch.CurrentFile);
            if (!index.TryGetValue(key, out var rows))
            {
                index[key] = rows = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            if (!rows.TryGetValue(touch.RowName, out var mods))
            {
                rows[touch.RowName] = mods = [];
            }

            if (!mods.Contains(touch.ModName, StringComparer.OrdinalIgnoreCase))
            {
                mods.Add(touch.ModName);
            }
        }

        return index;
    }

    /// <summary>
    /// The Library mods that change this row of the file. A change outside the rows (Defaults,
    /// RowStruct) reaches every row, so every mod that changes any row of that table counts.
    /// Empty while the mods switch is off or the Library hasn't been read.
    /// </summary>
    private IReadOnlyList<string> ModsFor(ChangedDataFileViewModel file, string itemName)
    {
        if (!HasMyModRows || !_myModRows!.TryGetValue(LibraryModRows.TableKey(file.RelativePath), out var rows))
        {
            return [];
        }

        if (itemName == ChangedDataFileViewModel.EveryRowItemName)
        {
            return [.. rows.Values.SelectMany(mods => mods).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        return rows.TryGetValue(itemName, out var mods) ? mods : [];
    }

    /// <summary>Whether a row passes the search (its file's name, its item or its field) and the mods switch.</summary>
    private bool RowPasses(ChangedDataFileViewModel file, string itemName, string fieldName, bool fileNameMatches)
    {
        if (IsSearching && !fileNameMatches)
        {
            var search = SearchText.Trim();
            if (!itemName.Contains(search, StringComparison.OrdinalIgnoreCase) && !fieldName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return !HasMyModRows || ModsFor(file, itemName).Count > 0;
    }

    private bool FileNameMatches(ChangedDataFileViewModel file) =>
        IsSearching && file.RelativePath.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A file stays in the list while one of its rows passes (read from the counts, so rows aren't built for it).</summary>
    private bool FilePasses(ChangedDataFileViewModel file)
    {
        var fileNameMatches = FileNameMatches(file);
        var keys = file.RowKeys().ToList();
        if (keys.Count == 0)
        {
            // A new or removed file with no rows: only its name can match.
            return !HasMyModRows && (!IsSearching || fileNameMatches);
        }

        return keys.Any(key => RowPasses(file, key.ItemName, key.FieldName, fileNameMatches));
    }

    /// <summary>Re-fills the file list from the report with the search and the mods switch, keeping the file shown when it's still there.</summary>
    private void ApplyFilter()
    {
        var previous = SelectedFile;
        ChangedFiles.Clear();
        foreach (var file in _allFiles.Where(FilePasses))
        {
            ChangedFiles.Add(file);
        }

        SelectedFile = (previous is null ? null : ChangedFiles.FirstOrDefault(f => f.RelativePath == previous.RelativePath))
            ?? ChangedFiles.FirstOrDefault();
        RefreshSelectedFileRows();
        OnPropertyChanged(nameof(HasMyModRows));
    }

    private void RefreshSelectedFileRows()
    {
        if (SelectedFile is not { } file)
        {
            SelectedFileRows = [];
        }
        else
        {
            var fileNameMatches = FileNameMatches(file);
            SelectedFileRows = [.. file.Rows
                .Where(row => RowPasses(file, row.ItemName, row.FieldName, fileNameMatches))
                .Select(row => HasMyModRows ? row with { ModNames = string.Join(", ", ModsFor(file, row.ItemName)) } : row)];
        }

        RaiseEmptyTexts();
    }

    private void ShowReport(WeeklyChangeReport? report)
    {
        _allFiles.Clear();

        if (report is null)
        {
            ChangedFiles.Clear();
            SummaryMessage = "No changes tracked yet — run Refresh game data at least twice, once now and again after a game update, to see what changed between them.";
            SelectedFile = null;
            RefreshSelectedFileRows();
            return;
        }

        var between = $"between {report.PreviousUpdateAt.LocalDateTime:d MMM yyyy, HH:mm} and {report.CurrentUpdateAt.LocalDateTime:d MMM yyyy, HH:mm}";
        SummaryMessage = (report.ChangedFiles.Count switch
        {
            0 => $"No game data files changed {between}",
            1 => $"1 game data file changed {between}",
            var n => $"{n} game data files changed {between}",
        }) + (BuildText(report) is { } build ? $" ({build})." : ".");

        // Rows aren't built here: only the file shown builds its own (ChangedDataFileViewModel.Rows).
        _allFiles.AddRange(report.ChangedFiles.Select(file => new ChangedDataFileViewModel(file)));

        // The same file again when this report has it (ApplyFilter keeps it by name), else the
        // first one, so the grid isn't empty until a file is clicked.
        ApplyFilter();
    }

    private void RaiseEmptyTexts()
    {
        OnPropertyChanged(nameof(FilesEmptyText));
        OnPropertyChanged(nameof(RowsEmptyText));
    }
}
