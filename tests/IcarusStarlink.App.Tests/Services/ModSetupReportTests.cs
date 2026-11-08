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

    /// <summary>The settings are read with the Merge queue on the UI thread, so a change made while the zip is built doesn't show half old, half new.</summary>
    [Fact]
    public void Capture_ReadsTheSettingsAtOnce()
    {
        _settings.LastSeenGameBuildId = "111";
        var report = new ModSetupReport(() => [], () => _settings);

        var build = report.Capture();
        _settings.LastSeenGameBuildId = "222";

        Assert.Contains("Mods last made ready for game build: 111", build());
    }

    /// <summary>
    /// Regression: "Game build" showed the build the mods were last made ready for, not the one
    /// installed, so a bug report after a game update named the old build.
    /// </summary>
    [Fact]
    public void Build_NamesTheInstalledBuild_AndTheOneTheModsWereMadeReadyFor()
    {
        var steamapps = Path.Combine(_tempDir, "steamapps");
        _settings.IcarusContentPath = Path.Combine(steamapps, "common", "Icarus", "Icarus", "Content");
        Directory.CreateDirectory(_settings.IcarusContentPath);
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1149460.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1149460\"\n\t\"buildid\"\t\t\"222\"\n}\n");
        _settings.LastSeenGameBuildId = "111";

        var text = new ModSetupReport(() => [], () => _settings).Capture()();

        Assert.Contains("Game build: 222", text);
        Assert.Contains("Mods last made ready for game build: 111", text);

        _settings.LastSeenGameBuildId = "222";
        Assert.DoesNotContain("made ready", new ModSetupReport(() => [], () => _settings).Capture()());
    }

    /// <summary>The " | " between a mod's parts stays plain: it only means something in a Markdown table, and Nexus shows "\|" as typed.</summary>
    [Fact]
    public void ToMarkdown_LeavesPipesPlain()
    {
        var markdown = ModSetupReport.ToMarkdown("== Library mods ==\nTidy Base 1.0 | EXMOD | Nexus mod 5\n");

        Assert.Contains("- Tidy Base 1.0 | EXMOD | Nexus mod 5", markdown);
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

    [Fact]
    public void ToMarkdown_BoldsEachPart_ListsEachLine_NestsIndentedLines_AndKeepsTheQueueNumbered()
    {
        var markdown = ModSetupReport.ToMarkdown(
            "== UE4SS ==\nUE4SS build: 3.0.1\nUE4SS mods:\n  Wireless_Power: on\n\n== Merge queue ==\nQueue, in the order the Merge page lists it:\n  1. Stack*Sizes\n");

        Assert.Equal(
            "## My Icarus mod setup\n\n**UE4SS**\n- UE4SS build: 3.0.1\n- UE4SS mods:\n  - Wireless\\_Power: on\n\n"
            + "**Merge queue**\n- Queue, in the order the Merge page lists it:\n  1. Stack\\*Sizes\n",
            markdown.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Regression: the user folder was hidden after the Markdown step, so a user name with '_' in
    /// it (escaped to "\_") no longer matched and went onto the clipboard.
    /// </summary>
    [Fact]
    public void CaptureMarkdown_AUserFolderWithAnUnderscore_IsHidden()
    {
        _settings.IcarusContentPath = @"C:\Users\John_Doe\Games\Icarus\Content";
        var report = new ModSetupReport(() => [], () => _settings);

        var markdown = report.CaptureMarkdown(@"C:\Users\John_Doe")();

        Assert.DoesNotContain("John", markdown);
        Assert.Contains("%USERPROFILE%", markdown);
    }
}
