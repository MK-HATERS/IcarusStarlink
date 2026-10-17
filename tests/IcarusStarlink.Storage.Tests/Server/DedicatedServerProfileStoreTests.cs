using IcarusStarlink.Core.Server;
using IcarusStarlink.Storage.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Server;

public sealed class DedicatedServerProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void SaveUpdateDelete_PersistAcrossInstances()
    {
        var store = new DedicatedServerProfileStore(_dir, NullLogger<DedicatedServerProfileStore>.Instance);
        var profile = new DedicatedServerProfile { Name = "Friends", InstallFolder = @"F:\IcarusServer", Port = 17778, SavedDirSuffix = "_2" };
        store.Save(profile);
        store.Save(new DedicatedServerProfile { Name = "Other", InstallFolder = @"F:\Other" });

        profile.Name = "Friends renamed";
        store.Save(profile);

        var reopened = new DedicatedServerProfileStore(_dir, NullLogger<DedicatedServerProfileStore>.Instance);
        Assert.Equal(2, reopened.GetAll().Count);
        var back = reopened.GetAll().Single(p => p.Id == profile.Id);
        Assert.Equal("Friends renamed", back.Name);
        Assert.Equal(17778, back.Port);
        Assert.Equal("_2", back.SavedDirSuffix);
        Assert.Equal("Other", reopened.GetAll()[1].Name);

        reopened.Delete(profile.Id);
        Assert.Single(new DedicatedServerProfileStore(_dir, NullLogger<DedicatedServerProfileStore>.Instance).GetAll());
    }

    private string FilePath => Path.Combine(_dir, "dedicated_servers.json");

    /// <summary>A file locked at startup loads as an empty list; saving that list used to wipe every profile.</summary>
    [Fact]
    public void LockedAtStartup_NeverSavesOverTheFile_AndSaysWhy()
    {
        var first = new DedicatedServerProfileStore(_dir, NullLogger<DedicatedServerProfileStore>.Instance);
        first.Save(new DedicatedServerProfile { Name = "Friends", InstallFolder = @"F:\IcarusServer" });
        var original = File.ReadAllText(FilePath);

        DedicatedServerProfileStore locked;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            locked = new DedicatedServerProfileStore(_dir, NullLogger<DedicatedServerProfileStore>.Instance);
        }

        Assert.Empty(locked.GetAll());
        Assert.Contains("couldn't be read", locked.LoadProblem);
        Assert.False(locked.Save(new DedicatedServerProfile { Name = "New", InstallFolder = @"F:\Other" }));
        Assert.False(locked.Delete(Guid.NewGuid()));

        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    /// <summary>A damaged file is kept aside before the first save over it.</summary>
    [Fact]
    public void CorruptFile_IsKeptAsideBeforeTheFirstSave()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var store = new DedicatedServerProfileStore(_dir, NullLogger<DedicatedServerProfileStore>.Instance);
        Assert.NotNull(store.LoadProblem);
        Assert.True(store.Save(new DedicatedServerProfile { Name = "New", InstallFolder = @"F:\Other" }));

        var kept = Assert.Single(Directory.GetFiles(_dir, "dedicated_servers.json.unreadable-*.bak"));
        Assert.Equal("{ not json", File.ReadAllText(kept));
        Assert.Single(new DedicatedServerProfileStore(_dir, NullLogger<DedicatedServerProfileStore>.Instance).GetAll());
    }

    [Fact]
    public void EverySave_KeepsThePreviousFileAsBak()
    {
        var store = new DedicatedServerProfileStore(_dir, NullLogger<DedicatedServerProfileStore>.Instance);
        var profile = new DedicatedServerProfile { Name = "First", InstallFolder = @"F:\IcarusServer" };
        store.Save(profile);
        var afterFirst = File.ReadAllText(FilePath);

        profile.Name = "Second";
        store.Save(profile);

        Assert.Equal(afterFirst, File.ReadAllText(FilePath + ".bak"));
        Assert.Null(store.LoadProblem);
    }
}
