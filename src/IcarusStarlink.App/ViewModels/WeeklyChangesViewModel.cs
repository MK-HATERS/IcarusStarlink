using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
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

    public string Title => "Weekly Changes";

    /// <summary>Exposed so this page's own "Refresh game data" shortcut can run the exact same
    /// command Settings' own button does, rather than duplicating the extraction logic here —
    /// the same reuse pattern NexusCatalogViewModel.Downloads already uses for a sibling page's
    /// command. SettingsViewModel is a DI singleton constructed eagerly at app startup, so this
    /// is never the thing that first brings it into existence.</summary>
    public SettingsViewModel Settings { get; }

    public ObservableCollection<WeeklyChangeReportSummary> ReportHistory { get; } = [];

    [ObservableProperty]
    private WeeklyChangeReportSummary? _selectedReport;

    public ObservableCollection<ChangedDataFileViewModel> ChangedFiles { get; } = [];

    [ObservableProperty]
    private ChangedDataFileViewModel? _selectedFile;

    [ObservableProperty]
    private string _summaryMessage = "";

    /// <param name="messenger">Injectable like Merge &amp; Install's, so a test can use its own instead of the shared Default.</param>
    public WeeklyChangesViewModel(IWeeklyChangeReportStore store, SettingsViewModel settings, IMessenger? messenger = null)
    {
        _store = store;
        Settings = settings;
        messenger ??= WeakReferenceMessenger.Default;

        // This VM is a DI singleton, constructed once on first navigation here — without this, a
        // report saved later (from Settings' Refresh game data, or this page's own shortcut)
        // wouldn't show up until some unrelated trigger happened to rebuild this page. Same
        // rationale as LibraryViewModel's own LibraryChangedMessage registration.
        messenger.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((WeeklyChangesViewModel)recipient).ReloadHistory());

        ReloadHistory();
    }

    partial void OnSelectedReportChanged(WeeklyChangeReportSummary? value) => ShowReport(value?.Report);

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
        $"{report.CurrentUpdateAt.LocalDateTime:d MMM yyyy, HH:mm} · {FilesChangedText(report.ChangedFiles.Count)}";

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
        : SelectedReport is null ? "Nothing to compare yet." : "No files changed in this update.";

    /// <summary>What the changes table says when it has no rows; null while it has some.</summary>
    public string? RowsEmptyText => SelectedReport is null
        ? "Refresh game data now and again after the next game update; what changed shows here."
        : ChangedFiles.Count == 0
            ? "No changes in this update."
            : SelectedFile is null
                ? "Pick a file on the left."
                : SelectedFile.Rows.Count == 0
                    ? $"{SelectedFile.StatusLabel}: no row-by-row changes to show."
                    : null;

    partial void OnSelectedFileChanged(ChangedDataFileViewModel? value) => OnPropertyChanged(nameof(RowsEmptyText));

    private void ShowReport(WeeklyChangeReport? report)
    {
        var previouslySelectedPath = SelectedFile?.RelativePath;

        ChangedFiles.Clear();

        if (report is null)
        {
            SummaryMessage = "No changes tracked yet — run Refresh game data at least twice, once now and again after a game update, to see what changed between them.";
            SelectedFile = null;
            RaiseEmptyTexts();
            return;
        }

        var between = $"between {report.PreviousUpdateAt.LocalDateTime:d MMM yyyy, HH:mm} and {report.CurrentUpdateAt.LocalDateTime:d MMM yyyy, HH:mm}";
        SummaryMessage = report.ChangedFiles.Count switch
        {
            0 => $"No game data files changed {between}.",
            1 => $"1 game data file changed {between}.",
            var n => $"{n} game data files changed {between}.",
        };

        foreach (var file in report.ChangedFiles)
        {
            ChangedFiles.Add(new ChangedDataFileViewModel(file));
        }

        // The same file again when this report has it, else the first one, so the grid isn't
        // empty until a file is clicked.
        SelectedFile = (previouslySelectedPath is null ? null : ChangedFiles.FirstOrDefault(f => f.RelativePath == previouslySelectedPath))
            ?? ChangedFiles.FirstOrDefault();
        RaiseEmptyTexts();
    }

    private void RaiseEmptyTexts()
    {
        OnPropertyChanged(nameof(FilesEmptyText));
        OnPropertyChanged(nameof(RowsEmptyText));
    }
}
