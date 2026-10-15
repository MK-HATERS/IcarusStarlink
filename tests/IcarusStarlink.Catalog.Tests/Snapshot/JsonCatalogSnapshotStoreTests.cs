using IcarusStarlink.Catalog;

namespace IcarusStarlink.Catalog.Tests.Snapshot;

public sealed class JsonCatalogSnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"Snapshot_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEntriesDatesAndTimestamp()
    {
        var store = new JsonCatalogSnapshotStore(Path.Combine(_dir, "Cache", "catalog_snapshot.json"));
        var entry = new CatalogEntry(CatalogSource.Daedalus, "id-1", "Cool Mod", "Someone", "1.0", "D", "All", 215, null, "https://x/readme", "https://x/a.pak", null, ["building"]);
        var fetchedAt = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        store.Save(new CatalogSnapshot(fetchedAt, [entry], [new RepoPushedDate("owner", "repo", fetchedAt.AddDays(-3))]));
        var loaded = store.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal(fetchedAt, loaded.FetchedAtUtc);
        var loadedEntry = Assert.Single(loaded.Entries);
        Assert.Equal(entry.Id, loadedEntry.Id);
        Assert.Equal(entry.Name, loadedEntry.Name);
        Assert.Equal(entry.CompatibleWeek, loadedEntry.CompatibleWeek);
        Assert.Equal(entry.Categories, loadedEntry.Categories);
        Assert.Equal("owner", Assert.Single(loaded.RepoDates).Owner);
    }

    [Fact]
    public void TryLoad_MissingOrCorruptFile_ReturnsNullNeverThrows()
    {
        var path = Path.Combine(_dir, "catalog_snapshot.json");
        var store = new JsonCatalogSnapshotStore(path);
        Assert.Null(store.TryLoad());

        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json");
        Assert.Null(store.TryLoad());
    }
}
