using IcarusStarlink.App.ViewModels;
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
}
