using IcarusStarlink.Storage.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Settings;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// Audit finding: a settings.json that merely couldn't be read at startup (sync client /
    /// antivirus lock) loaded as defaults, and the first Save then wrote those defaults over the
    /// user's real file. The original must be preserved beside it before that happens.
    /// </summary>
    [Fact]
    public void Save_AfterTheOriginalCouldNotBeReadAtStartup_PreservesTheOriginalBeforeOverwriting()
    {
        Directory.CreateDirectory(_dir);
        var settingsPath = Path.Combine(_dir, "settings.json");
        const string original = """{"IcarusContentPath":"E:\Icarus\Content","ThemeName":"Dark"}""";
        File.WriteAllText(settingsPath, original);

        AppSettingsService service;
        using (new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            service = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance);
        }

        Assert.Null(service.Current.IcarusContentPath);
        Assert.True(service.Save());

        var preserved = Assert.Single(Directory.GetFiles(_dir, "settings.json.unreadable-*.bak"));
        Assert.Equal(original, File.ReadAllText(preserved));
    }

    /// <summary>Review 2026-10-23: a settings.json that isn't valid JSON (a hand-edit gone wrong) loaded as defaults and was overwritten with no copy kept.</summary>
    [Fact]
    public void Save_AfterTheOriginalWasNotValidJson_PreservesTheOriginalBeforeOverwriting()
    {
        Directory.CreateDirectory(_dir);
        var settingsPath = Path.Combine(_dir, "settings.json");
        const string original = """{"IcarusContentPath":"E:\Icarus\Content",,}""";
        File.WriteAllText(settingsPath, original);

        var service = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance);

        Assert.Null(service.Current.IcarusContentPath);
        Assert.True(service.Save());
        Assert.True(service.Save());

        var preserved = Assert.Single(Directory.GetFiles(_dir, "settings.json.unreadable-*.bak"));
        Assert.Equal(original, File.ReadAllText(preserved));
    }

    /// <summary>The UI size and the Database page's "seen" record survive a restart.</summary>
    [Fact]
    public void Save_UiScaleAndCatalogSeen_RoundTrip()
    {
        Directory.CreateDirectory(_dir);
        var seenUpTo = new DateTimeOffset(2026, 10, 28, 14, 22, 12, TimeSpan.Zero);
        var service = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance);
        service.Current.UiScale = 1.3;
        service.Current.CatalogSeen.ImmDatabaseVersions["daedalus:a"] = "1.2";
        service.Current.CatalogSeen.ImmDatabaseSources.Add("Daedalus");
        service.Current.CatalogSeen.NexusSeenUpToUtc = seenUpTo;
        Assert.True(service.Save());

        var reloaded = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance).Current;

        Assert.Equal(1.3, reloaded.UiScale);
        Assert.Equal("1.2", reloaded.CatalogSeen.ImmDatabaseVersions["daedalus:a"]);
        Assert.Equal(["Daedalus"], reloaded.CatalogSeen.ImmDatabaseSources);
        Assert.Equal(seenUpTo, reloaded.CatalogSeen.NexusSeenUpToUtc);
    }

    /// <summary>A settings.json from before these existed loads with the defaults (100%, nothing seen yet).</summary>
    [Fact]
    public void Load_OlderSettingsFile_DefaultsUiScaleAndCatalogSeen()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"ThemeName":"Dark"}""");

        var settings = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance).Current;

        Assert.Equal(1.0, settings.UiScale);
        Assert.Empty(settings.CatalogSeen.ImmDatabaseVersions);
        Assert.Null(settings.CatalogSeen.NexusSeenUpToUtc);
    }

    /// <summary>Settings › Maintenance › Backups to keep survives a restart.</summary>
    [Fact]
    public void Save_BackupsToKeep_RoundTrip()
    {
        Directory.CreateDirectory(_dir);
        var service = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance);
        service.Current.MergedPakBackupsKept = 7;
        service.Current.ModBackupsKept = 4;
        service.Current.SaveBackupsKept = 25;
        service.Current.ServerBackupsKept = 3;
        Assert.True(service.Save());

        var reloaded = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance).Current;

        Assert.Equal(7, reloaded.MergedPakBackupsKept);
        Assert.Equal(4, reloaded.ModBackupsKept);
        Assert.Equal(25, reloaded.SaveBackupsKept);
        Assert.Equal(3, reloaded.ServerBackupsKept);
    }

    /// <summary>A settings.json from before Backups to keep existed keeps what the app always kept: 3 merged pak installs, 2 of each mod, 10 saves, 10 server backups.</summary>
    [Fact]
    public void Load_OlderSettingsFile_DefaultsBackupsToKeepToTheOldCounts()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"ThemeName":"Dark"}""");

        var settings = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance).Current;

        Assert.Equal(3, settings.MergedPakBackupsKept);
        Assert.Equal(2, settings.ModBackupsKept);
        Assert.Equal(10, settings.SaveBackupsKept);
        Assert.Equal(10, settings.ServerBackupsKept);
    }

    /// <summary>A hand-edited 0, a negative or a huge count is held to 1..50 where it's read, so the newest backup can't be pruned away.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(12, 12)]
    [InlineData(10_000, 50)]
    public void Load_BackupsToKeepOutOfRange_IsHeldToOneToFifty(int written, int read)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            $$"""{"MergedPakBackupsKept":{{written}},"ModBackupsKept":{{written}},"SaveBackupsKept":{{written}},"ServerBackupsKept":{{written}}}""");

        var service = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance);

        Assert.Equal(written, service.Current.MergedPakBackupsKept);
        Assert.Equal(read, IcarusStarlink.Core.Settings.BackupLimits.MergedPakKept(service));
        Assert.Equal(read, IcarusStarlink.Core.Settings.BackupLimits.ModKept(service));
        Assert.Equal(read, IcarusStarlink.Core.Settings.BackupLimits.SaveKept(service));
        Assert.Equal(read, IcarusStarlink.Core.Settings.BackupLimits.ServerKept(service));
    }

    [Fact]
    public void Save_NormalStartup_NeverWritesAPreservedCopy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"ThemeName":"Dark"}""");

        var service = new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance);
        service.Current.ThemeName = "Light";
        Assert.True(service.Save());

        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
        Assert.Equal("Light", new AppSettingsService(_dir, NullLogger<AppSettingsService>.Instance).Current.ThemeName);
    }
}
