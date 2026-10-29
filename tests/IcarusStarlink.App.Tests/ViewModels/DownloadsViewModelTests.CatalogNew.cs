using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The IMM Database tab's "new since your last visit" (classic IMM's Show New).</summary>
public sealed partial class DownloadsViewModelTests
{
    private static CatalogEntry ImmEntry(CatalogSource source, string id, string name, string version) =>
        new(source, id, name, "Author", version, "D", "", null, null, null, PakUrl: null, ExmodzUrl: null, Categories: []);

    [Fact]
    public async Task ImmDatabase_FirstLoadCountsAsSeen_ThenAddedAndUpdatedModsAreNewUntilMarkedSeen()
    {
        var settings = new FakeSettingsService();
        var daedalus = new FakeCatalogClient
        {
            Entries = [ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0"), ImmEntry(CatalogSource.Daedalus, "daedalus:b", "Bravo", "1.0")],
        };
        var jimk72 = new FakeCatalogClient { Entries = [ImmEntry(CatalogSource.Jimk72, "jimk72:c", "Charlie", "1")] };
        var harness = new Harness(_root) { Catalog = daedalus, Jimk72Catalog = jimk72, Settings = settings };

        var vm = harness.Build();
        await vm.GetOrFetchCatalogAsync();

        // First run ever: no wall of "new".
        Assert.Equal(3, vm.CatalogTotalCount);
        Assert.Equal(0, vm.CatalogNewCount);
        Assert.DoesNotContain(vm.CatalogEntries, row => row.HasNewTag);

        // The database gains Delta and Bravo moves to 2.0; Charlie's "1" listed as "1.0" is the same version.
        daedalus.Entries =
        [
            ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0"),
            ImmEntry(CatalogSource.Daedalus, "daedalus:b", "Bravo", "2.0"),
            ImmEntry(CatalogSource.Daedalus, "daedalus:d", "Delta", "1.0"),
        ];
        jimk72.Entries = [ImmEntry(CatalogSource.Jimk72, "jimk72:c", "Charlie", "1.0")];
        await vm.RefreshCatalogCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.CatalogNewCount);
        Assert.Equal("Updated", vm.CatalogEntries.Single(r => r.Name == "Bravo").NewTag);
        Assert.Equal("New", vm.CatalogEntries.Single(r => r.Name == "Delta").NewTag);
        Assert.Null(vm.CatalogEntries.Single(r => r.Name == "Charlie").NewTag);

        vm.ShowNewOnly = true;
        Assert.Equal(["Bravo", "Delta"], vm.CatalogEntries.Select(r => r.Name));
        Assert.Contains("New only", vm.CatalogFilterSummary);

        vm.MarkCatalogSeenCommand.Execute(null);

        Assert.Equal(0, vm.CatalogNewCount);
        Assert.False(vm.ShowNewOnly);
        Assert.Equal(4, vm.CatalogEntries.Count);
        Assert.Equal("2.0", settings.Current.CatalogSeen.ImmDatabaseVersions["daedalus:b"]);

        // Next session, same settings: nothing new.
        var nextSession = new Harness(_root) { Catalog = daedalus, Jimk72Catalog = jimk72, Settings = settings }.Build();
        await nextSession.GetOrFetchCatalogAsync();
        Assert.Equal(0, nextSession.CatalogNewCount);
    }

    /// <summary>A source that was down at the first load doesn't come back as a wall of "new" later.</summary>
    [Fact]
    public async Task ImmDatabase_ASourceDownAtTheFirstLoad_GetsItsOwnBaselineWhenItAnswers()
    {
        var settings = new FakeSettingsService();
        var daedalus = new FakeCatalogClient { Entries = [ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0")] };
        var jimk72 = new FakeCatalogClient();
        var vm = new Harness(_root) { Catalog = daedalus, Jimk72Catalog = jimk72, Settings = settings }.Build();
        await vm.GetOrFetchCatalogAsync();

        jimk72.Entries = [ImmEntry(CatalogSource.Jimk72, "jimk72:c", "Charlie", "1"), ImmEntry(CatalogSource.Jimk72, "jimk72:e", "Echo", "1")];
        daedalus.Entries = [ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0"), ImmEntry(CatalogSource.Daedalus, "daedalus:f", "Foxtrot", "1.0")];
        await vm.RefreshCatalogCommand.ExecuteAsync(null);

        Assert.Equal(["Foxtrot"], vm.CatalogEntries.Where(r => r.HasNewTag).Select(r => r.Name));
    }

    /// <summary>
    /// When the New tags count as seen: a Database tab opened this session is recorded when Starlink
    /// closes (RecordSeen); a tab never opened keeps its tags. The tab header carries the count.
    /// </summary>
    [Fact]
    public async Task DatabasePage_RecordsTheImmDatabaseAsSeenOnlyAfterItsTabWasOpened()
    {
        var settings = new FakeSettingsService();
        var daedalus = new FakeCatalogClient { Entries = [ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0")] };
        var vm = new Harness(_root) { Catalog = daedalus, Jimk72Catalog = new FakeCatalogClient(), Settings = settings }.Build();
        await vm.GetOrFetchCatalogAsync();
        daedalus.Entries = [ImmEntry(CatalogSource.Daedalus, "daedalus:a", "Alpha", "1.0"), ImmEntry(CatalogSource.Daedalus, "daedalus:g", "Golf", "1.0")];
        await vm.RefreshCatalogCommand.ExecuteAsync(null);

        var page = DatabaseViewModel.ForTests(null!, vm);
        Assert.Equal("IMM Database (1 new)", page.ImmDatabaseTabHeader);

        // Only the Nexus tab was looked at: closing keeps the IMM Database's New tag.
        page.OnPageShown();
        page.RecordSeen();
        Assert.Equal(1, vm.CatalogNewCount);

        // Away to another page, then back on the IMM Database tab: now it counts as seen.
        page.OnPageLeft();
        page.SelectedTab = DatabaseViewModel.ImmDatabaseTab;
        page.RecordSeen();
        Assert.Equal(1, vm.CatalogNewCount);

        page.OnPageShown();
        page.RecordSeen();
        Assert.Equal(0, vm.CatalogNewCount);
        Assert.Equal("IMM Database", page.ImmDatabaseTabHeader);
        Assert.True(settings.Current.CatalogSeen.ImmDatabaseVersions.ContainsKey("daedalus:g"));
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.UnregisterAll(page);
    }
}
