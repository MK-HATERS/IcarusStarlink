using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

public class DiagnosticsExporterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _logsDirectory;
    private readonly string _settingsFilePath;
    private readonly string _outputZipPath;

    public DiagnosticsExporterTests()
    {
        _logsDirectory = Path.Combine(_tempDir, "Logs");
        _settingsFilePath = Path.Combine(_tempDir, "settings.json");
        _outputZipPath = Path.Combine(_tempDir, "diagnostics.zip");
        Directory.CreateDirectory(_logsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Export_BundlesEveryLogFile()
    {
        File.WriteAllText(Path.Combine(_logsDirectory, "icarusstarlink-20260101.log"), "log line one");
        File.WriteAllText(Path.Combine(_logsDirectory, "icarusstarlink-20260102.log"), "log line two");
        File.WriteAllText(_settingsFilePath, """{"ThemeName": "Icarus"}""");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        Assert.Contains(archive.Entries, e => e.FullName == "Logs/icarusstarlink-20260101.log");
        Assert.Contains(archive.Entries, e => e.FullName == "Logs/icarusstarlink-20260102.log");
    }

    /// <summary>Regression: a corrupt settings.json — exactly what a diagnostics zip exists to capture — threw out of the redaction step and aborted the whole export.</summary>
    [Fact]
    public void Export_SettingsJsonIsNotValidJson_StillExportsItVerbatim()
    {
        File.WriteAllText(_settingsFilePath, "{ this is not valid json");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        var entry = Assert.Single(archive.Entries, e => e.FullName == "settings.json");
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("{ this is not valid json", reader.ReadToEnd());
    }

    [Fact]
    public void Export_IncludesSettingsJsonWithNonSensitiveFieldsIntact()
    {
        File.WriteAllText(_settingsFilePath, """{"ThemeName": "Icarus", "IcarusContentPath": "C:\\Game\\Content"}""");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        var entry = Assert.Single(archive.Entries, e => e.FullName == "settings.json");
        using var reader = new StreamReader(entry.Open());
        var settings = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        Assert.Equal("Icarus", settings["ThemeName"]!.GetValue<string>());
        Assert.Equal("C:\\Game\\Content", settings["IcarusContentPath"]!.GetValue<string>());
    }

    [Fact]
    public void Export_RedactsFieldsWhoseNameLooksSensitive()
    {
        // Defensive: nothing in AppSettings today is actually a secret (keys/passwords live in
        // Windows Credential Manager), but the exporter shouldn't trust that forever.
        File.WriteAllText(_settingsFilePath,
            """{"ThemeName": "Icarus", "SomeApiKey": "real-secret-value", "GitHubToken": "ghp_real", "FtpPassword": "hunter2"}""");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        var entry = Assert.Single(archive.Entries, e => e.FullName == "settings.json");
        using var reader = new StreamReader(entry.Open());
        var settings = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        Assert.Equal("[REDACTED]", settings["SomeApiKey"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", settings["GitHubToken"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", settings["FtpPassword"]!.GetValue<string>());
        Assert.Equal("Icarus", settings["ThemeName"]!.GetValue<string>()); // untouched
    }

    /// <summary>Redaction reaches nested objects and lists too, not just top-level fields.</summary>
    [Fact]
    public void Export_RedactsNestedSensitiveFields()
    {
        File.WriteAllText(_settingsFilePath, """{"Servers": [{"Name": "Mine", "AdminPassword": "hunter2"}], "Nexus": {"ApiKey": "secret"}}""");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        var entry = Assert.Single(archive.Entries, e => e.FullName == "settings.json");
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("secret", text);
        Assert.Contains("Mine", text);
    }

    /// <summary>
    /// Regression: logs, crash reports and settings went into the zip as they are, so every path
    /// in them showed the player's Windows user name (C:\Users\name\...) in a zip attached to
    /// public bug reports. The user folder now shows as %USERPROFILE%.
    /// </summary>
    [Fact]
    public void Export_ShowsTheWindowsUserFolderAsUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
        {
            return;
        }

        File.WriteAllText(Path.Combine(_logsDirectory, "icarusstarlink-20260101.log"),
            $"Saved {profile}\\Downloads\\a.zip; forward {profile.Replace('\\', '/')}/x; other {profile}name\\y");
        File.WriteAllText(Path.Combine(_logsDirectory, "crash-20260101-120000-000.txt"), $"at {profile}\\AppData\\Local\\Icarus");
        File.WriteAllText(_settingsFilePath, new JsonObject { ["DownloadsFolder"] = profile + "\\Downloads" }.ToJsonString());

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        var log = ReadEntry(archive, "Logs/icarusstarlink-20260101.log");
        Assert.Contains(@"%USERPROFILE%\Downloads\a.zip", log);
        Assert.Contains("%USERPROFILE%/x", log);
        Assert.Contains($"{profile}name\\y", log); // a longer sibling folder is someone else's
        var crash = ReadEntry(archive, "Logs/crash-20260101-120000-000.txt");
        Assert.Contains(@"%USERPROFILE%\AppData\Local\Icarus", crash);
        Assert.DoesNotContain(profile, crash, StringComparison.OrdinalIgnoreCase);
        var settings = JsonNode.Parse(ReadEntry(archive, "settings.json"))!.AsObject();
        Assert.Equal(@"%USERPROFILE%\Downloads", settings["DownloadsFolder"]!.GetValue<string>());
        Assert.Contains("Windows user folder:", ReadEntry(archive, "system-info.txt"));
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = Assert.Single(archive.Entries, e => e.FullName == name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Regression: the chosen file was deleted before writing, so a failed export left the user's existing zip gone.</summary>
    [Fact]
    public void Export_Fails_LeavesTheExistingZipAlone()
    {
        File.WriteAllText(_outputZipPath, "the user's existing file");
        var blocker = _outputZipPath + ".tmp";
        Directory.CreateDirectory(blocker); // the temp zip can't be created where a folder is

        Assert.ThrowsAny<Exception>(() => DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath));

        Assert.Equal("the user's existing file", File.ReadAllText(_outputZipPath));
    }

    [Fact]
    public void Export_NoLogsDirectory_StillProducesAZipWithJustSystemInfoAndSettings()
    {
        Directory.Delete(_logsDirectory);
        File.WriteAllText(_settingsFilePath, """{"ThemeName": "Dark"}""");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.FullName == "system-info.txt");
        Assert.Contains(archive.Entries, e => e.FullName == "settings.json");
    }

    [Fact]
    public void Export_AlwaysIncludesSystemInfoWithVersionAndOsDetails()
    {
        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        var entry = Assert.Single(archive.Entries, e => e.FullName == "system-info.txt");
        using var reader = new StreamReader(entry.Open());
        var content = reader.ReadToEnd();
        Assert.Contains("IcarusStarlink version:", content);
        Assert.Contains("OS:", content);
        Assert.Contains(".NET runtime:", content);
    }

    [Fact]
    public void Export_BundlesCrashReportTextFilesAlongsideLogs()
    {
        File.WriteAllText(Path.Combine(_logsDirectory, "crash-20260101-120000.txt"), "crash report content");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        Assert.Contains(archive.Entries, e => e.FullName == "Logs/crash-20260101-120000.txt");
    }

    [Fact]
    public void Export_OneLogFileExclusivelyLocked_SkipsItButStillIncludesTheRestAndSettings()
    {
        // Simulates the real scenario this app hits every time diagnostics are exported while it's
        // still running: today's own log file is open (Serilog holds its own handle on it). One
        // locked file must not sink the whole export — every other file, and settings.json, still
        // matter.
        var lockedLogPath = Path.Combine(_logsDirectory, "icarusstarlink-locked.log");
        File.WriteAllText(lockedLogPath, "will be locked");
        File.WriteAllText(Path.Combine(_logsDirectory, "icarusstarlink-readable.log"), "readable content");
        File.WriteAllText(_settingsFilePath, """{"ThemeName": "Icarus"}""");

        using var exclusiveLock = new FileStream(lockedLogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        Assert.DoesNotContain(archive.Entries, e => e.FullName == "Logs/icarusstarlink-locked.log");
        Assert.Contains(archive.Entries, e => e.FullName == "Logs/icarusstarlink-readable.log");
        Assert.Contains(archive.Entries, e => e.FullName == "settings.json");
    }

    [Fact]
    public void Export_NoSettingsFile_StillProducesAZipWithSystemInfoAndLogs()
    {
        File.WriteAllText(Path.Combine(_logsDirectory, "icarusstarlink-20260101.log"), "log line");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.FullName == "system-info.txt");
        Assert.DoesNotContain(archive.Entries, e => e.FullName == "settings.json");
    }

    /// <summary>
    /// Regression guard: ZipArchiveMode.Create opens with FileMode.CreateNew under the hood, which
    /// throws IOException if outputZipPath already exists. SettingsViewModel's own SaveFileDialog
    /// already shows a native "this file exists, overwrite?" prompt and gets a "yes" whenever the
    /// user picks an existing path — but that prompt only confirms intent, it doesn't delete
    /// anything — so confirming it used to still end in "Export failed: ... already exists.",
    /// directly contradicting the answer the user just gave.
    /// </summary>
    [Fact]
    public void Export_OutputPathAlreadyExists_OverwritesItInsteadOfThrowing()
    {
        File.WriteAllText(_outputZipPath, "this is not a zip file — stands in for a real stale previous export");
        File.WriteAllText(Path.Combine(_logsDirectory, "icarusstarlink-20260101.log"), "log line");

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        Assert.Contains(archive.Entries, e => e.FullName == "Logs/icarusstarlink-20260101.log");
    }

    // --- Bug-report kit: mod-setup.txt, last-game-crash.txt, ue4ss-log-tail.txt ---

    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>A crash folder as Icarus writes one: its CrashContext (with the ids it carries) and a minidump.</summary>
    private string MakeCrashesFolder(int secondsSinceStart = 125)
    {
        var crashDir = Path.Combine(_tempDir, "Crashes", "UE4CC-Windows-ABC_0000");
        Directory.CreateDirectory(crashDir);
        File.WriteAllText(Path.Combine(crashDir, "CrashContext.runtime-xml"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <FGenericCrashContext>
                <RuntimeProperties>
                    <CrashType>Crash</CrashType>
                    <ErrorMessage>Unhandled Exception: EXCEPTION_ACCESS_VIOLATION reading {UserProfile}\AppData\thing</ErrorMessage>
                    <EngineVersion>4.27.2-0+++UE4+Release-4.27</EngineVersion>
                    <SecondsSinceStart>{secondsSinceStart}</SecondsSinceStart>
                    <CommandLine>Icarus -dx12 -SecretArg</CommandLine>
                    <BaseDir>{UserProfile}\Games\Icarus\Binaries\Win64</BaseDir>
                    <MachineId>289810A14D926B6BC48AE4A426D364FD</MachineId>
                    <LoginId>289810a14d926b6bc48ae4a426d364fd</LoginId>
                    <EpicAccountId>d985d944a36141fb804fdafbfee1839c</EpicAccountId>
                    <PCallStack>UE4SS 0x00007ffbfc300000 + 2c5680 KERNELBASE 0x00007ffd180e0000 + 1427f</PCallStack>
                </RuntimeProperties>
            </FGenericCrashContext>
            """);
        File.WriteAllText(Path.Combine(crashDir, "UE4Minidump.dmp"), "minidump bytes");
        return Path.Combine(_tempDir, "Crashes");
    }

    /// <summary>A Content folder whose UE4SS.log names a file under the user's own folder.</summary>
    private string MakeContentWithUe4ssLog(int lines = 3)
    {
        var content = Path.Combine(_tempDir, "Icarus", "Content");
        var loader = Path.Combine(_tempDir, "Icarus", "Binaries", "Win64", "ue4ss");
        Directory.CreateDirectory(content);
        Directory.CreateDirectory(loader);
        File.WriteAllLines(Path.Combine(loader, "UE4SS.log"),
            [.. Enumerable.Range(1, lines).Select(i => $"log line {i}"), $"Loaded mod from {UserProfile}\\Desktop\\MyMod"]);
        return content;
    }

    private string ReadEntry(string name)
    {
        using var archive = ZipFile.OpenRead(_outputZipPath);
        var entry = Assert.Single(archive.Entries, e => e.FullName == name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void Export_WithBugReport_AddsTheThreeEntriesWithoutIdsUserNameOrMinidump()
    {
        var bugReport = new DiagnosticsBugReport(
            () => $"Library mods:\n  Better Bags 1.2 (Nexus, mod 123)\nGame: {UserProfile}\\Games\\Icarus\\Content",
            MakeCrashesFolder(), MakeContentWithUe4ssLog());

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath, bugReport);

        var setup = ReadEntry("mod-setup.txt");
        var crash = ReadEntry("last-game-crash.txt");
        var log = ReadEntry("ue4ss-log-tail.txt");
        Assert.Contains("Better Bags 1.2 (Nexus, mod 123)", setup);
        Assert.Contains("EXCEPTION_ACCESS_VIOLATION", crash);
        Assert.Contains("4.27.2-0+++UE4+Release-4.27", crash);
        Assert.Contains("UE4SS in the crash stack: yes", crash);
        Assert.Contains("log line 3", log);
        foreach (var text in new[] { setup, crash, log })
        {
            Assert.DoesNotContain(UserProfile, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("289810A14D926B6BC48AE4A426D364FD", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("d985d944a36141fb804fdafbfee1839c", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecretArg", text);
        }

        Assert.Contains("%USERPROFILE%", setup);
        Assert.Contains("%USERPROFILE%", log);
        using var archive = ZipFile.OpenRead(_outputZipPath);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A crash after more than a day of play keeps the whole hours, not just the hours past the last full day.</summary>
    [Fact]
    public void Export_WithBugReport_CrashAfter25Hours_ShowsAllTheHours()
    {
        var bugReport = new DiagnosticsBugReport(null, MakeCrashesFolder(secondsSinceStart: (25 * 3600) + 62), null);

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath, bugReport);

        Assert.Contains("Game had run for: 25:01:02", ReadEntry("last-game-crash.txt"));
    }

    [Fact]
    public void Export_WithBugReport_UsesOnlyTheEndOfALongUe4ssLog()
    {
        var bugReport = new DiagnosticsBugReport(null, Path.Combine(_tempDir, "NoCrashes"), MakeContentWithUe4ssLog(lines: 1000));

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath, bugReport);

        var log = ReadEntry("ue4ss-log-tail.txt").Replace("\r\n", "\n");
        Assert.Contains("log line 1000\n", log);
        Assert.DoesNotContain("log line 1\n", log);
    }

    [Fact]
    public void Export_WithBugReport_NothingToFind_SaysSoInEachEntry()
    {
        var bugReport = new DiagnosticsBugReport(null, Path.Combine(_tempDir, "NoCrashes"), null);

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath, bugReport);

        Assert.Contains("No game crash found", ReadEntry("last-game-crash.txt"));
        Assert.Contains("No UE4SS.log found", ReadEntry("ue4ss-log-tail.txt"));
        Assert.Contains("not available", ReadEntry("mod-setup.txt"));
    }

    /// <summary>A failing setup reader must not sink the export: its error goes in the entry, the rest of the zip is still made.</summary>
    [Fact]
    public void Export_WithBugReport_SetupReaderThrows_StillExportsAndSaysWhy()
    {
        var bugReport = new DiagnosticsBugReport(() => throw new IOException("library locked"), Path.Combine(_tempDir, "NoCrashes"), null);

        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath, bugReport);

        Assert.Contains("library locked", ReadEntry("mod-setup.txt"));
        Assert.Contains("No game crash found", ReadEntry("last-game-crash.txt"));
    }

    [Fact]
    public void Export_WithoutBugReport_AddsNoKitEntries()
    {
        DiagnosticsExporter.Export(_logsDirectory, _settingsFilePath, _outputZipPath);

        using var archive = ZipFile.OpenRead(_outputZipPath);
        Assert.DoesNotContain(archive.Entries, e => e.FullName is "mod-setup.txt" or "last-game-crash.txt" or "ue4ss-log-tail.txt");
    }
}
