using IcarusStarlink.Core.Server;
using IcarusStarlink.Storage.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Server;

public class FtpSiteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private FtpSiteStore CreateStore() => new(_dir, NullLogger<FtpSiteStore>.Instance);

    private static FtpSiteProfile MakeSite(Guid id, string name = "My Server") => new()
    {
        Id = id, Name = name, Host = "ftp.example.com", Port = 21, Username = "icarus", RemotePath = "/mods",
    };

    [Fact]
    public void Constructor_NoFileYet_StartsEmpty()
    {
        Assert.Empty(CreateStore().GetAll());
    }

    [Fact]
    public void Save_PersistsAcrossStoreInstances()
    {
        var id = Guid.NewGuid();
        CreateStore().Save(MakeSite(id, "Dedicated Server"));

        var reopened = CreateStore();

        Assert.Equal("Dedicated Server", Assert.Single(reopened.GetAll()).Name);
    }

    [Fact]
    public void Save_SameIdTwice_ReplacesRatherThanDuplicating()
    {
        var id = Guid.NewGuid();
        var store = CreateStore();
        store.Save(MakeSite(id, "First name"));
        store.Save(MakeSite(id, "Updated name"));

        var site = Assert.Single(store.GetAll());
        Assert.Equal("Updated name", site.Name);
    }

    [Fact]
    public void Delete_RemovesTheSiteAndPersists()
    {
        var id = Guid.NewGuid();
        var store = CreateStore();
        store.Save(MakeSite(id));
        store.Delete(id);

        Assert.Empty(CreateStore().GetAll());
    }

    [Fact]
    public void Delete_UnknownId_DoesNothing()
    {
        var store = CreateStore();

        var exception = Record.Exception(() => store.Delete(Guid.NewGuid()));

        Assert.Null(exception);
    }

    [Fact]
    public void Save_RoundTripsEveryField()
    {
        var id = Guid.NewGuid();
        var site = new FtpSiteProfile
        {
            Id = id, Name = "Full Fields", Host = "203.0.113.5", Port = 2121, Username = "admin",
            RemotePath = "/home/icarus/mods", EncryptionMode = FtpEncryptionMode.Explicit,
        };
        CreateStore().Save(site);

        var reloaded = Assert.Single(CreateStore().GetAll());

        Assert.Equal(site.Id, reloaded.Id);
        Assert.Equal(site.Host, reloaded.Host);
        Assert.Equal(site.Port, reloaded.Port);
        Assert.Equal(site.Username, reloaded.Username);
        Assert.Equal(site.RemotePath, reloaded.RemotePath);
        Assert.Equal(site.EncryptionMode, reloaded.EncryptionMode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
