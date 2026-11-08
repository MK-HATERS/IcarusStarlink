using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Weekly Changes history picker read "(0 file(s) changed)".</summary>
public class WeeklyChangesViewModelTests
{
    [Theory]
    [InlineData(0, "no changes")]
    [InlineData(1, "1 file changed")]
    [InlineData(5, "5 files changed")]
    public void FilesChangedText_ReadsNaturally(int count, string expected) =>
        Assert.Equal(expected, WeeklyChangesViewModel.FilesChangedText(count));

    [Fact]
    public void DisplayFor_DateThenCount()
    {
        var at = new DateTimeOffset(2026, 10, 22, 15, 8, 0, TimeSpan.Zero);
        var report = new WeeklyChangeReport(at.AddDays(-7), at,
            [new ChangedDataFile("Crafting/D_Fuel.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: [], FieldChanges: [])]);

        var display = WeeklyChangesViewModel.DisplayFor(report);

        Assert.EndsWith(" · 1 file changed", display);
        Assert.StartsWith($"{at.LocalDateTime:d MMM yyyy, HH:mm}", display);
    }

    [Fact]
    public void ChangedFileSummary_OneChange_IsSingular()
    {
        var file = new ChangedDataFileViewModel(new ChangedDataFile("Crafting/D_Fuel.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: ["Old_Row"], FieldChanges: []));

        Assert.Equal("Modified · 1 change", file.Summary);
    }

    [Fact]
    public void ChangedFile_OnlyItsDefaultsChanged_ShowsOneRowForTheWholeTable()
    {
        var file = new ChangedDataFileViewModel(
            new ChangedDataFile("Crafting/D_Fuel.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: [], FieldChanges: []) { ChangedTableParts = ["Defaults"] });

        Assert.Equal("Modified · 1 change", file.Summary);
        var row = Assert.Single(file.Rows);
        Assert.Equal("Defaults", row.FieldName);
        Assert.Equal("defaults changed", row.ChangeKind);
    }

    // ---- Remove a report ----

    private static WeeklyChangeReport Report(int daysAgo, int changedFiles) => new(
        DateTimeOffset.UtcNow.AddDays(-daysAgo - 7),
        DateTimeOffset.UtcNow.AddDays(-daysAgo),
        Enumerable.Range(0, changedFiles)
            .Select(i => new ChangedDataFile($"Crafting/D_Fuel{i}.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: [], FieldChanges: []))
            .ToList());

    /// <summary>Settings isn't used by the remove flow, so the harness leaves it out.</summary>
    private sealed class Harness
    {
        public FakeStore Store { get; } = new();
        public FakeDialogs Dialogs { get; } = new();

        public WeeklyChangesViewModel Build() => new(Store, settings: null!, new WeakReferenceMessenger(), Dialogs);
    }

    [Fact]
    public void RemoveReport_Yes_RemovesTheShownReportAndShowsTheNextNewest()
    {
        var harness = new Harness();
        var older = Report(daysAgo: 5, changedFiles: 3);
        var empty = Report(daysAgo: 0, changedFiles: 0);
        harness.Store.Reports.AddRange([empty, older]);
        var vm = harness.Build();
        Assert.Same(empty, vm.SelectedReport!.Report);

        vm.RemoveReportCommand.Execute(null);

        Assert.Equal([older], harness.Store.Reports);
        Assert.Same(older, Assert.Single(vm.ReportHistory).Report);
        Assert.Same(older, vm.SelectedReport!.Report);
        Assert.Contains("no changes", Assert.Single(harness.Dialogs.Messages));
    }

    [Fact]
    public void RemoveReport_No_KeepsIt()
    {
        var harness = new Harness();
        harness.Dialogs.Answer = false;
        var report = Report(daysAgo: 0, changedFiles: 0);
        harness.Store.Reports.Add(report);
        var vm = harness.Build();

        vm.RemoveReportCommand.Execute(null);

        Assert.Single(harness.Store.Reports);
        Assert.Same(report, vm.SelectedReport!.Report);
        Assert.Equal(0, harness.Store.RemoveCallCount);
    }

    [Fact]
    public void RemoveReport_TheLastOne_LeavesTheEmptyPage_AndCantRunWithNothingPicked()
    {
        var harness = new Harness();
        harness.Store.Reports.Add(Report(daysAgo: 0, changedFiles: 0));
        var vm = harness.Build();
        Assert.True(vm.RemoveReportCommand.CanExecute(null));

        vm.RemoveReportCommand.Execute(null);

        Assert.Empty(vm.ReportHistory);
        Assert.Null(vm.SelectedReport);
        Assert.False(vm.RemoveReportCommand.CanExecute(null));
        Assert.StartsWith("No changes tracked yet", vm.SummaryMessage);
    }

    [Fact]
    public void RemoveReport_FileCantBeDeleted_SaysSoAndKeepsIt()
    {
        var harness = new Harness();
        harness.Store.FailRemove = true;
        var report = Report(daysAgo: 0, changedFiles: 0);
        harness.Store.Reports.Add(report);
        var vm = harness.Build();

        vm.RemoveReportCommand.Execute(null);

        Assert.Same(report, vm.SelectedReport!.Report);
        Assert.StartsWith("Couldn't remove", vm.SummaryMessage);
    }

    private sealed class FakeStore : IWeeklyChangeReportStore
    {
        public List<WeeklyChangeReport> Reports { get; } = [];
        public bool FailRemove { get; set; }
        public int RemoveCallCount { get; private set; }
        public WeeklyChangeReport? Current => Reports.FirstOrDefault();
        public IReadOnlyList<WeeklyChangeReport> History => Reports;

        public void Save(WeeklyChangeReport report) => Reports.Insert(0, report);

        public bool Remove(WeeklyChangeReport report)
        {
            RemoveCallCount++;
            return !FailRemove && Reports.Remove(report);
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool Answer { get; set; } = true;
        public List<string> Messages { get; } = [];

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            Messages.Add(message);
            return Answer;
        }

        public RenamePromptResult PromptRename(string currentName, string description, string? resetValue, string resetLabel, string resetTooltip, string title, string fieldLabel) =>
            new(Cancelled: true, NewDisplayName: null);
    }
}
