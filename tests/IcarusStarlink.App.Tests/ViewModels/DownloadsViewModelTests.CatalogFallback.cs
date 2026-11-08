using System.Net.Http;
using IcarusStarlink.Catalog;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>A catalog source that can't be reached keeps the mods Starlink already knew about instead of emptying the list.</summary>
public sealed partial class DownloadsViewModelTests
{
    private sealed class FakeCatalogSnapshotStore(CatalogSnapshot? snapshot) : ICatalogSnapshotStore
    {
        public CatalogSnapshot? TryLoad() => snapshot;

        public void Save(CatalogSnapshot snapshot)
        {
        }
    }

    /// <summary>Regression: one source down replaced the catalog with the other source's mods, so its rows vanished and the update check called them up to date.</summary>
    [Fact]
    public async Task RefreshCatalog_OneSourceDown_KeepsItsModsFromTheLastLoad()
    {
        var daedalusEntry = ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0");
        var jimk72Entry = ImmEntry(CatalogSource.Jimk72, "jimk72:c", "Charlie", "1");
        var harness = new Harness(_root)
        {
            Catalog = new FakeCatalogClient { Entries = [daedalusEntry] },
            Jimk72Catalog = new FakeCatalogClient { Entries = [jimk72Entry] },
        };
        var vm = harness.Build();
        await vm.GetOrFetchCatalogAsync();
        harness.Catalog.Error = new HttpRequestException("down");

        await vm.RefreshCatalogCommand.ExecuteAsync(null);

        var catalog = await vm.GetOrFetchCatalogAsync();
        Assert.Contains(catalog, e => e.Id == daedalusEntry.Id);
        Assert.Contains(catalog, e => e.Id == jimk72Entry.Id);
        Assert.Equal(["Daedalus"], vm.LastFailedCatalogSources);
        Assert.Contains("from the last load", vm.CatalogStatusMessage);
    }

    /// <summary>Regression: an offline start more than six hours after the last fetch showed no mods while the snapshot was on disk.</summary>
    [Fact]
    public async Task OfflineStart_WithASnapshotOlderThanSixHours_ShowsItInsteadOfNothing()
    {
        var snapshot = new CatalogSnapshot(
            DateTimeOffset.UtcNow - TimeSpan.FromHours(7),
            [ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0"), ImmEntry(CatalogSource.Jimk72, "jimk72:c", "Charlie", "1")],
            []);
        var harness = new Harness(_root)
        {
            Catalog = new FakeCatalogClient { Error = new HttpRequestException("offline") },
            Jimk72Catalog = new FakeCatalogClient { Error = new HttpRequestException("offline") },
            CatalogSnapshots = new FakeCatalogSnapshotStore(snapshot),
        };

        var vm = harness.Build();

        Assert.Equal(2, (await vm.GetOrFetchCatalogAsync()).Count);
        Assert.Contains("last successful load", vm.CatalogStatusMessage);
        Assert.Equal(["Daedalus", "Jimk72"], vm.LastFailedCatalogSources.Order());
    }

    /// <summary>Regression: a snapshot that couldn't be used faulted the load, so the table stayed empty with no status and nothing was fetched.</summary>
    [Fact]
    public async Task DamagedSnapshot_FallsBackToFetching()
    {
        var pushed = DateTimeOffset.UtcNow;
        var snapshot = new CatalogSnapshot(
            DateTimeOffset.UtcNow,
            [ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0")],
            [new RepoPushedDate("owner", "repo", pushed), new RepoPushedDate("owner", "repo", pushed)]);
        var harness = new Harness(_root)
        {
            Catalog = new FakeCatalogClient { Entries = [ImmEntry(CatalogSource.Daedalus, "daedalus:b", "Bravo", "1.0")] },
            CatalogSnapshots = new FakeCatalogSnapshotStore(snapshot),
        };

        var vm = harness.Build();
        await vm.GetOrFetchCatalogAsync();

        Assert.True(harness.Catalog.FetchCount > 0);
        Assert.StartsWith("Loaded", vm.CatalogStatusMessage);
    }
}
