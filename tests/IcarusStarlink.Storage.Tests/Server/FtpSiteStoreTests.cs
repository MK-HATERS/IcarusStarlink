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
            ModsPath = "gameserver/Content/Paks/mods", Win64Path = "gameserver/Binaries/Win64",
        };
        CreateStore().Save(site);

        var reloaded = Assert.Single(CreateStore().GetAll());

        Assert.Equal(site.Id, reloaded.Id);
        Assert.Equal(site.Host, reloaded.Host);
        Assert.Equal(site.Port, reloaded.Port);
        Assert.Equal(site.Username, reloaded.Username);
        Assert.Equal(site.RemotePath, reloaded.RemotePath);
        Assert.Equal(site.EncryptionMode, reloaded.EncryptionMode);
        Assert.Equal(site.ModsPath, reloaded.ModsPath);
        Assert.Equal(site.Win64Path, reloaded.Win64Path);
    }

    /// <summary>
    /// ModsPath/Win64Path are nullable specifically so a site saved before these fields existed —
    /// or one whose host matches the SurvivalServers-shaped default and never sets an override —
    /// keeps working unchanged; this is the regression test for that back-compat contract.
    /// </summary>
    [Fact]
    public void Save_NeitherPerSitePathOverrideSet_RoundTripsAsNullRatherThanEmptyString()
    {
        var id = Guid.NewGuid();
        CreateStore().Save(MakeSite(id));

        var reloaded = Assert.Single(CreateStore().GetAll());

        Assert.Null(reloaded.ModsPath);
        Assert.Null(reloaded.Win64Path);
    }

    [Fact]
    public void Constructor_OneMalformedEntryAmongValidOnes_SkipsOnlyThatEntryInsteadOfDiscardingEveryOne()
    {
        // JsonFileStore.LoadList's whole reason to exist over plain Load<List<T>>: a single
        // malformed record (bad hand-edit, a future schema change) must not silently wipe out
        // every OTHER perfectly-valid saved site too.
        Directory.CreateDirectory(_dir);
        var filePath = Path.Combine(_dir, "ftp_sites.json");
        var goodId = Guid.NewGuid();
        File.WriteAllText(filePath, $$"""
            [
              {"Id": "{{goodId}}", "Name": "Good Site", "Host": "ftp.example.com", "Port": 21, "Username": "icarus", "RemotePath": "/mods"},
              {"Id": "not-a-guid", "Name": "Bad Site", "Host": "x", "Username": "u"}
            ]
            """);

        var store = CreateStore();

        var site = Assert.Single(store.GetAll());
        Assert.Equal("Good Site", site.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
