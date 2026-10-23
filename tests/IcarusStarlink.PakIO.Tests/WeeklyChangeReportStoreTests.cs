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

    // A real bug found live: this used to be a fixed calendar date (2026-08-27). Save's own
    // RetentionDays pruning compares against the REAL DateTimeOffset.UtcNow, so once enough real
    // time passed for that fixed date (and especially its own Now.AddDays(-N) offsets below) to
    // fall outside the retention window, tests relying on them surviving a reopen started failing
    // on their own, with nothing in this file having changed — confirmed live: by 2026-10-14, both
    // History_ReturnsNewestFirst and Save_CalledAgain_AccumulatesInHistoryInsteadOfOverwriting had
    // started failing this exact way. Computed relative to the real current time instead so this
    // can never go stale again — truncated to the date (midnight UTC) purely for a clean, stable
    // value within a single test run, not because the time-of-day component matters anywhere here.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.Date;

    /// <summary>Saving over a report with the same timestamp replaces it in the history too, instead of listing it twice.</summary>
    [Fact]
    public void Save_SameTimestampAgain_ReplacesTheEntry()
    {
        var store = CreateStore();
        store.Save(MakeReport(Now));
        var again = MakeReport(Now);

        store.Save(again);

        Assert.Same(again, Assert.Single(store.History));
        Assert.Single(CreateStore().History);
    }

    /// <summary>The file name is the Gregorian date whatever calendar the PC uses, so Prune finds the file Save wrote.</summary>
    [Fact]
    public void Save_OnAPcWithAnotherCalendar_NamesTheFileByTheGregorianDate()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            CreateStore().Save(MakeReport(Now));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }

        Assert.Equal(Now.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir, "*.json", SearchOption.AllDirectories))));
    }

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
