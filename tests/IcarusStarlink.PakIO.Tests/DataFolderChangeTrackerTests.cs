using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Tests;

public class DataFolderChangeTrackerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _previousDir;
    private readonly string _currentDir;
    private static readonly DateTimeOffset PreviousAt = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentAt = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    public DataFolderChangeTrackerTests()
    {
        _previousDir = Path.Combine(_tempDir, "Previous");
        _currentDir = Path.Combine(_tempDir, "Current");
        Directory.CreateDirectory(_previousDir);
        Directory.CreateDirectory(_currentDir);
    }

    private void WritePrevious(string relativePath, string json) => Write(_previousDir, relativePath, json);
    private void WriteCurrent(string relativePath, string json) => Write(_currentDir, relativePath, json);

    private static void Write(string root, string relativePath, string json)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void Compute_FieldValueChangedOnExistingRow_ReportsFieldChange()
    {
        WritePrevious("Crafting/D_Fuel.json", """{"Rows":[{"Name":"Composter","ResourceFlowRate":10}]}""");
        WriteCurrent("Crafting/D_Fuel.json", """{"Rows":[{"Name":"Composter","ResourceFlowRate":20}]}""");

        var report = DataFolderChangeTracker.Compute(_previousDir, _currentDir, PreviousAt, CurrentAt);

        var file = Assert.Single(report.ChangedFiles);
        Assert.Equal("Crafting/D_Fuel.json", file.RelativePath);
        Assert.False(file.IsNewFile);
        Assert.False(file.IsRemovedFile);
        var change = Assert.Single(file.FieldChanges);
        Assert.Equal("Composter", change.ItemName);
        Assert.Equal("ResourceFlowRate", change.FieldName);
        Assert.Equal(20, change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void Compute_FileOnlyInCurrent_ReportsAsNewFileWithNewItemChanges()
    {
        WriteCurrent("Traits/D_NewTable.json", """{"Rows":[{"Name":"Widget","Amount":5}]}""");

        var report = DataFolderChangeTracker.Compute(_previousDir, _currentDir, PreviousAt, CurrentAt);

        var file = Assert.Single(report.ChangedFiles);
        Assert.True(file.IsNewFile);
        var change = Assert.Single(file.FieldChanges);
        Assert.True(change.IsNewItem);
    }

    [Fact]
    public void Compute_FileOnlyInPrevious_ReportsAsRemovedFileWithRemovedRowNames()
    {
        WritePrevious("Traits/D_GoneTable.json", """{"Rows":[{"Name":"Widget","Amount":5},{"Name":"Gadget","Amount":3}]}""");

        var report = DataFolderChangeTracker.Compute(_previousDir, _currentDir, PreviousAt, CurrentAt);

        var file = Assert.Single(report.ChangedFiles);
        Assert.True(file.IsRemovedFile);
        Assert.Empty(file.FieldChanges);
        Assert.Equal(["Gadget", "Widget"], file.RemovedRowNames.OrderBy(n => n));
    }

    [Fact]
    public void Compute_WholeRowRemovedFromExistingFile_IsCapturedDespiteTableDifferNotSeeingIt()
    {
        // TableDiffer only iterates the *new* side's rows (its own doc comment explains why) — a
        // row present in previous but entirely absent from current would otherwise vanish
        // silently. This is the supplementary pass that catches it.
        WritePrevious("Crafting/D_Fuel.json", """{"Rows":[{"Name":"Composter","Amount":10},{"Name":"Generator","Amount":5}]}""");
        WriteCurrent("Crafting/D_Fuel.json", """{"Rows":[{"Name":"Composter","Amount":10}]}""");

        var report = DataFolderChangeTracker.Compute(_previousDir, _currentDir, PreviousAt, CurrentAt);

        var file = Assert.Single(report.ChangedFiles);
        Assert.False(file.IsRemovedFile);
        Assert.Empty(file.FieldChanges);
        Assert.Equal(["Generator"], file.RemovedRowNames);
    }

    [Fact]
    public void Compute_IdenticalFile_IsExcludedFromChangedFiles()
    {
        WritePrevious("Crafting/D_Fuel.json", """{"Rows":[{"Name":"Composter","Amount":10}]}""");
        WriteCurrent("Crafting/D_Fuel.json", """{"Rows":[{"Name":"Composter","Amount":10}]}""");

        var report = DataFolderChangeTracker.Compute(_previousDir, _currentDir, PreviousAt, CurrentAt);

        Assert.Empty(report.ChangedFiles);
    }

    [Fact]
    public void Compute_NoFilesEitherSide_ReturnsEmptyReportNotAnError()
    {
        var report = DataFolderChangeTracker.Compute(_previousDir, _currentDir, PreviousAt, CurrentAt);

        Assert.Empty(report.ChangedFiles);
        Assert.Equal(PreviousAt, report.PreviousUpdateAt);
        Assert.Equal(CurrentAt, report.CurrentUpdateAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
