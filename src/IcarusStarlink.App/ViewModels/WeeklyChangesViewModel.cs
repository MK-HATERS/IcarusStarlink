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

    /// <summary>Exposed so this page's own "Update data folder" shortcut can run the exact same
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

    public WeeklyChangesViewModel(IWeeklyChangeReportStore store, SettingsViewModel settings)
    {
        _store = store;
        Settings = settings;

        // This VM is a DI singleton, constructed once on first navigation here — without this, a
        // report saved later (from Settings' Update data folder, or this page's own shortcut)
        // wouldn't show up until some unrelated trigger happened to rebuild this page. Same
        // rationale as LibraryViewModel's own LibraryChangedMessage registration.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((WeeklyChangesViewModel)recipient).ReloadHistory());

        ReloadHistory();
    }

    partial void OnSelectedReportChanged(WeeklyChangeReportSummary? value) => ShowReport(value?.Report);

    private void ReloadHistory()
    {
        var previouslySelected = SelectedReport?.Report.CurrentUpdateAt;

        ReportHistory.Clear();
        foreach (var report in _store.History)
        {
            ReportHistory.Add(new WeeklyChangeReportSummary(report, DisplayFor(report)));
        }

        // Re-selecting (rather than just calling ShowReport directly) keeps SelectedReport and the
        // ComboBox's own binding in sync — a plain ShowReport call here would leave the ComboBox
        // showing the previous selection's text even though the underlying list just changed.
        SelectedReport = previouslySelected is { } at
            ? ReportHistory.FirstOrDefault(r => r.Report.CurrentUpdateAt == at) ?? ReportHistory.FirstOrDefault()
            : ReportHistory.FirstOrDefault();

        if (ReportHistory.Count == 0)
        {
            ShowReport(null);
        }
    }

    private static string DisplayFor(WeeklyChangeReport report) =>
        $"{report.CurrentUpdateAt.LocalDateTime:MMM d, yyyy · h:mm tt} ({report.ChangedFiles.Count} file(s) changed)";

    private void ShowReport(WeeklyChangeReport? report)
    {
        var previouslySelectedPath = SelectedFile?.RelativePath;

        ChangedFiles.Clear();

        if (report is null)
        {
            SummaryMessage = "No changes tracked yet — run Update data folder at least twice, once now and again after a game update, to see what changed between them.";
            SelectedFile = null;
            return;
        }

        SummaryMessage = report.ChangedFiles.Count == 0
            ? $"No JSON changes between {report.PreviousUpdateAt.LocalDateTime:g} and {report.CurrentUpdateAt.LocalDateTime:g}."
            : $"{report.ChangedFiles.Count} JSON file(s) changed between {report.PreviousUpdateAt.LocalDateTime:g} and {report.CurrentUpdateAt.LocalDateTime:g}.";

        foreach (var file in report.ChangedFiles)
        {
            ChangedFiles.Add(new ChangedDataFileViewModel(file));
        }

        SelectedFile = previouslySelectedPath is null
            ? null
            : ChangedFiles.FirstOrDefault(f => f.RelativePath == previouslySelectedPath);
    }
}
