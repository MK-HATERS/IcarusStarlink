using System.Text.Json;
using IcarusStarlink.PakIO.DataChanges;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.PakIO.Tests;

public class WeeklyChangeReportStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private WeeklyChangeReportStore CreateStore() => new(_dir, NullLogger<WeeklyChangeReportStore>.Instance);

    private static WeeklyChangeReport MakeReport(DateTimeOffset currentUpdateAt) => new(
        currentUpdateAt.AddDays(-7),
        currentUpdateAt,
        [new ChangedDataFile("Crafting/D_Fuel.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: ["Gone"], FieldChanges: [])]);

    private static readonly DateTimeOffset Now = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Current_NoFileYet_IsNull()
    {
        Assert.Null(CreateStore().Current);
    }

    [Fact]
    public void History_NoFileYet_IsEmpty()
    {
        Assert.Empty(CreateStore().History);
    }

    [Fact]
    public void Save_PersistsAcrossStoreInstances()
    {
        CreateStore().Save(MakeReport(Now));

        var reopened = CreateStore();

        Assert.NotNull(reopened.Current);
        Assert.Equal("Crafting/D_Fuel.json", Assert.Single(reopened.Current.ChangedFiles).RelativePath);
        Assert.Equal(["Gone"], reopened.Current.ChangedFiles[0].RemovedRowNames);
    }

    [Fact]
    public void Save_CalledAgain_AccumulatesInHistoryInsteadOfOverwriting()
    {
        var store = CreateStore();
        store.Save(MakeReport(Now.AddDays(-1)));
        var secondReport = MakeReport(Now) with { ChangedFiles = [] };

        store.Save(secondReport);

        var reopened = CreateStore();
        Assert.Equal(2, reopened.History.Count);
        Assert.Empty(reopened.Current!.ChangedFiles);
    }

    [Fact]
    public void History_ReturnsNewestFirst()
    {
        var store = CreateStore();
        store.Save(MakeReport(Now.AddDays(-10)));
        store.Save(MakeReport(Now));
        store.Save(MakeReport(Now.AddDays(-5)));

        var history = CreateStore().History;

        Assert.Equal(3, history.Count);
        Assert.Equal(Now, history[0].CurrentUpdateAt);
        Assert.Equal(Now.AddDays(-5), history[1].CurrentUpdateAt);
        Assert.Equal(Now.AddDays(-10), history[2].CurrentUpdateAt);
    }

    [Fact]
    public void Save_PrunesEntriesOlderThanRetentionWindow()
    {
        var store = CreateStore();
        store.Save(MakeReport(DateTimeOffset.UtcNow.AddDays(-45)));

        store.Save(MakeReport(DateTimeOffset.UtcNow));

        var reopened = CreateStore();
        Assert.Single(reopened.History);
    }

    [Fact]
    public void Save_NeverPrunesCurrentEvenIfOlderThanRetentionWindow()
    {
        var store = CreateStore();

        store.Save(MakeReport(DateTimeOffset.UtcNow.AddDays(-45)));

        Assert.NotNull(store.Current);
        Assert.Single(store.History);
    }

    [Fact]
    public void Save_UpdatesCurrentImmediately_NotOnlyAfterReopening()
    {
        var store = CreateStore();

        store.Save(MakeReport(Now));

        Assert.NotNull(store.Current);
    }

    [Fact]
    public void Construct_MigratesARealLegacySingleFile_ThenRemovesIt()
    {
        Directory.CreateDirectory(_dir);
        var legacyPath = Path.Combine(_dir, "weekly_changes.json");
        var legacyReport = MakeReport(Now);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacyReport, new JsonSerializerOptions { WriteIndented = true }));

        var store = CreateStore();

        Assert.NotNull(store.Current);
        Assert.Equal("Crafting/D_Fuel.json", Assert.Single(store.Current.ChangedFiles).RelativePath);
        Assert.False(File.Exists(legacyPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
