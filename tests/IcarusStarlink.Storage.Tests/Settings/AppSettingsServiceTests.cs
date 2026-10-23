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
