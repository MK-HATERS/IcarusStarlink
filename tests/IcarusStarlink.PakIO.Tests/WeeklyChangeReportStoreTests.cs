using IcarusStarlink.PakIO.DataChanges;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.PakIO.Tests;

public class WeeklyChangeReportStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private WeeklyChangeReportStore CreateStore() => new(_dir, NullLogger<WeeklyChangeReportStore>.Instance);

    private static WeeklyChangeReport MakeReport() => new(
        new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
        [new ChangedDataFile("Crafting/D_Fuel.json", IsNewFile: false, IsRemovedFile: false, RemovedRowNames: ["Gone"], FieldChanges: [])]);

    [Fact]
    public void Current_NoFileYet_IsNull()
    {
        Assert.Null(CreateStore().Current);
    }

    [Fact]
    public void Save_PersistsAcrossStoreInstances()
    {
        CreateStore().Save(MakeReport());

        var reopened = CreateStore();

        Assert.NotNull(reopened.Current);
        Assert.Equal("Crafting/D_Fuel.json", Assert.Single(reopened.Current.ChangedFiles).RelativePath);
        Assert.Equal(["Gone"], reopened.Current.ChangedFiles[0].RemovedRowNames);
    }

    [Fact]
    public void Save_CalledAgain_OverwritesRatherThanAccumulating()
    {
        var store = CreateStore();
        store.Save(MakeReport());
        var secondReport = MakeReport() with { ChangedFiles = [] };

        store.Save(secondReport);

        Assert.Empty(CreateStore().Current!.ChangedFiles);
    }

    [Fact]
    public void Save_UpdatesCurrentImmediately_NotOnlyAfterReopening()
    {
        var store = CreateStore();

        store.Save(MakeReport());

        Assert.NotNull(store.Current);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
