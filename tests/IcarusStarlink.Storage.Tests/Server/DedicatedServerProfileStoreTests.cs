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
}
