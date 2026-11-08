using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Diffing;
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

    /// <summary>A report's old values also tell Starlink which mods are old copies, so removing one with changes warns.</summary>
    [Fact]
    public void RemoveReport_WithChanges_WarnsTheOldCopyCheckLosesIt()
    {
        var harness = new Harness();
        harness.Store.Reports.Add(Report(daysAgo: 0, changedFiles: 2));
        var vm = harness.Build();

        vm.RemoveReportCommand.Execute(null);

        Assert.Contains("old values", Assert.Single(harness.Dialogs.Messages));
        Assert.Equal(ThemedConfirmSeverity.Warning, Assert.Single(harness.Dialogs.Severities));
    }

    [Fact]
    public void RemoveReport_Empty_AsksPlainly()
    {
        var harness = new Harness();
        harness.Store.Reports.Add(Report(daysAgo: 0, changedFiles: 0));
        var vm = harness.Build();

        vm.RemoveReportCommand.Execute(null);

        Assert.DoesNotContain("old values", Assert.Single(harness.Dialogs.Messages));
        Assert.Equal(ThemedConfirmSeverity.Question, Assert.Single(harness.Dialogs.Severities));
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

    // ---- Search, rows my mods change, the game build ----

    private static FieldChange Change(string file, string item, string field, int oldValue, int newValue) =>
        new(file, item, field, JsonValue.Create(oldValue), JsonValue.Create(newValue), ValueSemantic.Scalar);

    /// <summary>A patch that changed the canteen and a fuel, the bread, and the Defaults of the food table.</summary>
    private static WeeklyChangeReport PatchReport() => new(
        DateTimeOffset.UtcNow.AddDays(-7),
        DateTimeOffset.UtcNow,
        [
            new ChangedDataFile("Crafting/D_Fuel.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: ["Old_Coal"],
                FieldChanges: [Change("Crafting/D_Fuel.json", "Wood", "BurnTime", 10, 12), Change("Crafting/D_Fuel.json", "Canteen_Fuel", "Weight", 1, 2)]),
            new ChangedDataFile("Items/D_ItemsStatic.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: [],
                FieldChanges: [Change("Items/D_ItemsStatic.json", "Canteen", "Durability", 100, 150), Change("Items/D_ItemsStatic.json", "Bread", "Weight", 20, 25)]),
            new ChangedDataFile("Items/D_Food.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: [], FieldChanges: []) { ChangedTableParts = ["Defaults"] },
        ]);

    private static WeeklyChangesViewModel Build(WeeklyChangeReport report, Func<IReadOnlyList<ModRowTouch>>? myModRows = null)
    {
        var harness = new Harness();
        harness.Store.Reports.Add(report);
        return new WeeklyChangesViewModel(harness.Store, settings: null!, new WeakReferenceMessenger(), harness.Dialogs, myModRows);
    }

    [Fact]
    public void Search_ByItemName_ShowsOnlyTheFilesAndRowsWithIt()
    {
        var vm = Build(PatchReport());

        vm.SearchText = "canteen";

        Assert.Equal(["Crafting/D_Fuel.json", "Items/D_ItemsStatic.json"], vm.ChangedFiles.Select(f => f.RelativePath));
        Assert.Equal("Crafting/D_Fuel.json", vm.SelectedFile!.RelativePath);
        Assert.Equal(["Canteen_Fuel"], vm.SelectedFileRows.Select(r => r.ItemName));

        vm.SelectedFile = vm.ChangedFiles[1];

        Assert.Equal(["Canteen"], vm.SelectedFileRows.Select(r => r.ItemName));
    }

    [Fact]
    public void Search_ByFieldName_FindsTheRowsWithThatField()
    {
        var vm = Build(PatchReport());

        vm.SearchText = "Weight";

        Assert.Equal(["Crafting/D_Fuel.json", "Items/D_ItemsStatic.json"], vm.ChangedFiles.Select(f => f.RelativePath));
        Assert.Equal(["Canteen_Fuel"], vm.SelectedFileRows.Select(r => r.ItemName));
    }

    [Fact]
    public void Search_ByFileName_ShowsEveryRowOfThatFile()
    {
        var vm = Build(PatchReport());

        vm.SearchText = "d_fuel";

        Assert.Equal("Crafting/D_Fuel.json", Assert.Single(vm.ChangedFiles).RelativePath);
        Assert.Equal(3, vm.SelectedFileRows.Count);
    }

    [Fact]
    public void Search_NothingMatches_SaysSo_AndClearingItShowsEverythingAgain()
    {
        var vm = Build(PatchReport());

        vm.SearchText = "nothing like this";

        Assert.Empty(vm.ChangedFiles);
        Assert.Null(vm.SelectedFile);
        Assert.Contains("match", vm.FilesEmptyText);

        vm.SearchText = "";

        Assert.Equal(3, vm.ChangedFiles.Count);
        Assert.Equal(3, vm.SelectedFileRows.Count);
        Assert.Null(vm.FilesEmptyText);
    }

    [Fact]
    public async Task OnlyMyModRows_ShowsOnlyRowsALibraryModChanges_AndNamesTheMod()
    {
        var vm = Build(PatchReport(), () =>
        [
            new ModRowTouch("Better Canteens", "Items-D_ItemsStatic.json", "Canteen"),
            new ModRowTouch("Better Canteens", "Crafting-D_Fuel.json", "Old_Coal"),
            new ModRowTouch("Bread Mod", "Items-D_ItemsStatic.json", "Canteen"),
            new ModRowTouch("Pie Mod", "Items-D_Food.json", "Pie"),
            new ModRowTouch("Unrelated", "Items-D_Tools.json", "Wood"),
        ]);
        Assert.True(vm.CanShowOnlyMyModRows);

        vm.ShowOnlyMyModRows = true;
        await vm.MyModRowsLoad;

        Assert.Equal(["Crafting/D_Fuel.json", "Items/D_ItemsStatic.json", "Items/D_Food.json"], vm.ChangedFiles.Select(f => f.RelativePath));
        Assert.Equal(["Old_Coal"], vm.SelectedFileRows.Select(r => r.ItemName));
        Assert.Equal("Better Canteens", vm.SelectedFileRows[0].ModNames);

        vm.SelectedFile = vm.ChangedFiles[1];
        var canteen = Assert.Single(vm.SelectedFileRows);
        Assert.Equal("Canteen", canteen.ItemName);
        Assert.Equal("Better Canteens, Bread Mod", canteen.ModNames);

        // A Defaults change reaches every row, so a mod that changes any row of that table counts.
        vm.SelectedFile = vm.ChangedFiles[2];
        Assert.Equal("Pie Mod", Assert.Single(vm.SelectedFileRows).ModNames);

        vm.ShowOnlyMyModRows = false;

        Assert.Equal(3, vm.ChangedFiles.Count);
    }

    [Fact]
    public async Task OnlyMyModRows_NoModChangesAnything_SaysSo()
    {
        var vm = Build(PatchReport(), () => [new ModRowTouch("Unrelated", "Items-D_Tools.json", "Axe")]);

        vm.ShowOnlyMyModRows = true;
        await vm.MyModRowsLoad;

        Assert.Empty(vm.ChangedFiles);
        Assert.Contains("your mods", vm.FilesEmptyText);
    }

    [Fact]
    public async Task OnlyMyModRows_LibraryCantBeRead_TurnsOffAndSaysSo()
    {
        var vm = Build(PatchReport(), () => throw new IOException("locked"));

        vm.ShowOnlyMyModRows = true;
        await vm.MyModRowsLoad;

        Assert.False(vm.ShowOnlyMyModRows);
        Assert.Equal(3, vm.ChangedFiles.Count);
        Assert.Contains("Couldn't read", vm.MyModRowsStatus);
    }

    [Fact]
    public void OnlyMyModRows_WithoutALibrary_IsOff()
    {
        var vm = Build(PatchReport());

        Assert.False(vm.CanShowOnlyMyModRows);
    }

    [Fact]
    public void ChangedFile_RowsAreBuiltOnlyWhenOpened_TheSummaryComesFromCounts()
    {
        var file = new ChangedDataFileViewModel(PatchReport().ChangedFiles[0]);

        Assert.Equal("Modified · 3 changes", file.Summary);
        Assert.False(file.RowsBuilt);

        Assert.Equal(3, file.Rows.Count);
        Assert.True(file.RowsBuilt);
    }

    [Fact]
    public void Report_OnlyTheShownFileHasItsRowsBuilt()
    {
        var vm = Build(PatchReport());

        Assert.True(vm.ChangedFiles[0].RowsBuilt);
        Assert.False(vm.ChangedFiles[1].RowsBuilt);
    }

    [Fact]
    public void DisplayFor_NamesTheGameBuild_WhenTheReportHasOne()
    {
        var report = Report(daysAgo: 0, changedFiles: 2);

        Assert.EndsWith(" · 2 files changed · game build 25956278", WeeklyChangesViewModel.DisplayFor(report with { GameBuildId = "25956278" }));
        Assert.EndsWith(" · 2 files changed", WeeklyChangesViewModel.DisplayFor(report));
        // No Steam build number: data.pak's hash means nothing to a player.
        Assert.EndsWith(" · 2 files changed", WeeklyChangesViewModel.DisplayFor(report with { GameBuildId = "data-0123456789abcdef" }));
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
        public List<ThemedConfirmSeverity> Severities { get; } = [];

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            Messages.Add(message);
            Severities.Add(severity);
            return Answer;
        }

        public RenamePromptResult PromptRename(string currentName, string description, string? resetValue, string resetLabel, string resetTooltip, string title, string fieldLabel) =>
            new(Cancelled: true, NewDisplayName: null);
    }
}
