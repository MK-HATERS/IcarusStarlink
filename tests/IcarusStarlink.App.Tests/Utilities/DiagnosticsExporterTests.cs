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
}
