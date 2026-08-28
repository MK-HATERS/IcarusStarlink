using IcarusStarlink.Core.Catalog;
using IcarusStarlink.Storage.Catalog;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Catalog;

public class NexusWatchlistStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private NexusWatchlistStore CreateStore() => new(_dir, NullLogger<NexusWatchlistStore>.Instance);

    private static NexusWatchlistEntry MakeEntry(int id, string name = "Some Mod") => new()
    {
        NexusId = id, Url = $"https://www.nexusmods.com/icarus/mods/{id}", Name = name,
    };

    [Fact]
    public void Add_PersistsAcrossStoreInstances()
    {
        CreateStore().Add(MakeEntry(304, "Icarus Workshop"));

        var reopened = CreateStore();

        Assert.Equal("Icarus Workshop", Assert.Single(reopened.Entries).Name);
    }

    [Fact]
    public void Constructor_OneMalformedEntryAmongValidOnes_SkipsOnlyThatEntryInsteadOfDiscardingEveryOne()
    {
        Directory.CreateDirectory(_dir);
        var filePath = Path.Combine(_dir, "nexus_watchlist.json");
        File.WriteAllText(filePath, """
            [
              {"NexusId": 304, "Url": "https://www.nexusmods.com/icarus/mods/304", "Name": "Good Mod"},
              {"NexusId": "not-a-number", "Url": "https://www.nexusmods.com/icarus/mods/1", "Name": "Bad Mod"}
            ]
            """);

        var store = CreateStore();

        Assert.Equal("Good Mod", Assert.Single(store.Entries).Name);
    }

    [Fact]
    public void Add_SameNexusIdTwice_ReplacesRatherThanDuplicating()
    {
        var store = CreateStore();
        store.Add(MakeEntry(304, "First name"));
        store.Add(MakeEntry(304, "Updated name"));

        var entry = Assert.Single(store.Entries);
        Assert.Equal("Updated name", entry.Name);
    }

    [Fact]
    public void Remove_DeletesTheEntryAndPersists()
    {
        var store = CreateStore();
        store.Add(MakeEntry(304));
        store.Remove(304);

        Assert.Empty(CreateStore().Entries);
    }

    [Fact]
    public void UpdateName_ChangesTheNameAndPersists()
    {
        var store = CreateStore();
        store.Add(MakeEntry(304, "Placeholder"));
        store.UpdateName(304, "Renamed by user");

        Assert.Equal("Renamed by user", Assert.Single(CreateStore().Entries).Name);
    }

    [Fact]
    public void UpdateName_UnknownId_DoesNothing()
    {
        var store = CreateStore();

        var exception = Record.Exception(() => store.UpdateName(999, "N/A"));

        Assert.Null(exception);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Constructor_NoFileYet_StartsEmpty()
    {
        Assert.Empty(CreateStore().Entries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
