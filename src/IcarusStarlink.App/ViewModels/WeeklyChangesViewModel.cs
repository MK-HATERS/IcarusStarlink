using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Read-only view onto the single latest WeeklyChangeReport — mirrors classic IMM's own "This
/// Weeks Changes" feature (see WeeklyChangeReport's own doc comment), but as a dedicated nav page
/// instead of a synthetic library entry.
/// </summary>
public sealed partial class WeeklyChangesViewModel : ObservableObject
{
    private readonly IWeeklyChangeReportStore _store;

    public string Title => "Weekly Changes";

    public ObservableCollection<ChangedDataFileViewModel> ChangedFiles { get; } = [];

    [ObservableProperty]
    private ChangedDataFileViewModel? _selectedFile;

    [ObservableProperty]
    private string _summaryMessage = "";

    public WeeklyChangesViewModel(IWeeklyChangeReportStore store)
    {
        _store = store;

        // This VM is a DI singleton, constructed once on first navigation here — without this, a
        // report saved later (from Settings' Update data folder) wouldn't show up until some
        // unrelated trigger happened to rebuild this page. Same rationale as LibraryViewModel's
        // own LibraryChangedMessage registration.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((WeeklyChangesViewModel)recipient).Reload());

        Reload();
    }

    private void Reload()
    {
        var previouslySelectedPath = SelectedFile?.RelativePath;

        ChangedFiles.Clear();
        var report = _store.Current;

        if (report is null)
        {
            SummaryMessage = "No changes tracked yet — run Update data folder (in Settings) at least twice, once now and again after a game update, to see what changed between them.";
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
