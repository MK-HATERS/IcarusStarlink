using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.Services;

public class ModSetupReportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();

    public ModSetupReportTests()
    {
        _settings.IcarusContentPath = Path.Combine(_tempDir, "Icarus", "Content");
        Directory.CreateDirectory(_settings.IcarusContentPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static LibraryEntry Mod(string name, string version, string? source = null, int? nexusId = null, bool pak = false) => new()
    {
        FolderName = name,
        Name = name,
        Author = "Someone",
        Version = version,
        Description = "",
        FileName = name + ".EXMODZ",
        Source = source,
        NexusModId = nexusId,
        IsOpaquePak = pak,
    };

    [Fact]
    public void Build_ListsLibraryQueueOptionsInstallUe4ssAndGameData()
    {
        var mods = Path.Combine(_settings.IcarusContentPath!, "Paks", "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllLines(Path.Combine(mods, "ISL-Merged.txt"), ["Better Bags", "Stack Sizes"]);
        File.WriteAllText(Path.Combine(mods, "ISL_Merged_P.pak"), "");
        var loader = Path.Combine(_tempDir, "Icarus", "Binaries", "Win64", "ue4ss");
        Directory.CreateDirectory(loader);
        File.WriteAllLines(Path.Combine(loader, Ue4ssInstalledBuild.MarkerFileName), ["3.0.1", "abc1234"]);
        _settings.LastSeenGameBuildId = "20517935";
        _settings.LastDataFolderUpdatedAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var report = new ModSetupReport(
            () => [Mod("Better Bags", "1.2", "Nexus", 123), Mod("Stack Sizes", "2.0", pak: true)],
            () => _settings,
            _ => [new Ue4ssModState("WirelessPower", IsEnabled: true, IsInGame: true), new Ue4ssModState("QuickSave", IsEnabled: false, IsInGame: true)],
            () => new MergeQueueSnapshot(["Stack Sizes", "Better Bags"], "Stacks x2 · No weather", "Main"));

        var text = report.Capture()();

        Assert.Contains("Better Bags", text);
        Assert.Contains("1.2", text);
        Assert.Contains("Nexus mod 123", text);
        Assert.Contains("1. Stack Sizes", text);
        Assert.Contains("2. Better Bags", text);
        Assert.Contains("Stacks x2 · No weather", text);
        Assert.Contains("Main", text);
        Assert.Contains("ISL_Merged_P.pak", text);
        Assert.Contains("3.0.1", text);
        Assert.Contains("WirelessPower: on", text);
        Assert.Contains("QuickSave: off", text);
        Assert.Contains("ArcFission Library: not on", text);
        Assert.Contains("20517935", text);
        Assert.Contains("2026-11-01", text);
    }

    /// <summary>The Merge page's queue is read when Capture is called (on the UI thread), not later inside the background build.</summary>
    [Fact]
    public void Capture_ReadsTheQueueAtOnce_TheRestLater()
    {
        var queueReads = 0;
        var libraryReads = 0;
        var report = new ModSetupReport(
            () => { libraryReads++; return []; },
            () => _settings,
            mergeQueue: () => { queueReads++; return new MergeQueueSnapshot([], "No options active", null); });

        var build = report.Capture();

        Assert.Equal(1, queueReads);
        Assert.Equal(0, libraryReads);
        build();
        Assert.Equal(1, libraryReads);
    }

    /// <summary>One part that can't be read (a locked library) says so; the other parts are still written.</summary>
    [Fact]
    public void Build_OnePartThrows_TheOthersAreStillThere()
    {
        _settings.LastSeenGameBuildId = "20517935";
        var report = new ModSetupReport(() => throw new IOException("library locked"), () => _settings);

        var text = report.Capture()();

        Assert.Contains("library locked", text);
        Assert.Contains("20517935", text);
    }

    [Fact]
    public void Build_NoContentPath_SaysTheGameFolderIsNotSet()
    {
        _settings.IcarusContentPath = null;
        var report = new ModSetupReport(() => [], () => _settings);

        var text = report.Capture()();

        Assert.Contains("Game folder: not set", text);
    }
}
