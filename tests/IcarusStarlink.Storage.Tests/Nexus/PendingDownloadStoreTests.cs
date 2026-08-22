using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Storage.Nexus;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Nexus;

public class PendingDownloadStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private PendingDownloadStore CreateStore() => new(_dir, NullLogger<PendingDownloadStore>.Instance);

    private static PendingDownloadEntry MakeEntry(int modId, int fileId = 1, string fileName = "Some_Mod.zip") => new()
    {
        ModId = modId, FileId = fileId, FileName = fileName, LocalFilePath = $@"C:\fake\{fileName}", DownloadedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Add_PersistsAcrossStoreInstances()
    {
        CreateStore().Add(MakeEntry(290, 1234, "Nearby_Crafting.zip"));

        var reopened = CreateStore();

        Assert.Equal("Nearby_Crafting.zip", Assert.Single(reopened.Entries).FileName);
    }

    [Fact]
    public void Add_SameModAndFileIdTwice_ReplacesRatherThanDuplicating()
    {
        var store = CreateStore();
        store.Add(MakeEntry(290, 1234, "First.zip"));
        store.Add(MakeEntry(290, 1234, "Redownloaded.zip"));

        var entry = Assert.Single(store.Entries);
        Assert.Equal("Redownloaded.zip", entry.FileName);
    }

    [Fact]
    public void Add_SameModDifferentFileId_KeepsBoth()
    {
        var store = CreateStore();
        store.Add(MakeEntry(290, 1234));
        store.Add(MakeEntry(290, 5678));

        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void Remove_DeletesTheEntryAndPersists()
    {
        var store = CreateStore();
        store.Add(MakeEntry(290, 1234));
        store.Remove(290, 1234);

        Assert.Empty(CreateStore().Entries);
    }

    [Fact]
    public void Constructor_NoFileYet_StartsEmpty()
    {
        Assert.Empty(CreateStore().Entries);
    }

    [Fact]
    public void SetActivation_PersistsAcrossStoreInstances()
    {
        var store = CreateStore();
        store.Add(MakeEntry(290, 1234));
        store.SetActivation(290, 1234, "Nearby_Crafting", PendingDownloadActivationKind.Library);

        var entry = Assert.Single(CreateStore().Entries);
        Assert.Equal("Nearby_Crafting", entry.ActivatedFolderName);
        Assert.Equal(PendingDownloadActivationKind.Library, entry.ActivatedKind);
    }

    [Fact]
    public void SetActivation_DoesNotRemoveTheEntry()
    {
        var store = CreateStore();
        store.Add(MakeEntry(290, 1234));
        store.SetActivation(290, 1234, "Some_Mod", PendingDownloadActivationKind.Ue4ssMod);

        Assert.Single(store.Entries);
    }

    [Fact]
    public void SetActivation_UnknownModAndFileId_IsANoOp()
    {
        var store = CreateStore();
        store.SetActivation(999, 999, "Nothing", PendingDownloadActivationKind.Library);

        Assert.Empty(store.Entries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
