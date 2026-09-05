using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// "Export diagnostics zip (logs + sanitized settings, no API keys/tokens)" per the spec — bundles
/// a system-info.txt (app version, OS, .NET runtime), every rolled Serilog log file (which, since
/// LoggingActivityLog exists, includes the session's own activity history and any crash reports
/// written by CrashReportWriter — both surface here as *.log/crash-*.txt entries, not as separate
/// bundling logic), plus a redacted copy of settings.json into one zip a user can attach to a bug
/// report. Nothing this app stores in settings.json today is actually a secret (the Nexus API key
/// and FTP passwords both live in Windows Credential Manager via ICredentialStore, never in this
/// file) — the redaction pass below is defensive, in case a future settings field ever holds
/// something sensitive, not a fix for something currently exposed.
/// </summary>
public static class DiagnosticsExporter
{
    private static readonly string[] SensitiveKeyMarkers = ["key", "token", "password", "secret"];

    public static void Export(string logsDirectory, string settingsFilePath, string outputZipPath)
    {
        using var archive = ZipFile.Open(outputZipPath, ZipArchiveMode.Create);

        // A plain, always-present entry rather than relying on whoever reads this zip to notice a
        // version/OS line buried inside one specific day's rolling log (or find none at all, if
        // that day's log happened to roll over or wasn't written for some other reason) — every
        // crash report already carries this too (see CrashReportWriter), but a diagnostics export
        // isn't always crash-triggered, so it needs its own copy.
        var systemInfoEntry = archive.CreateEntry("system-info.txt");
        using (var writer = new StreamWriter(systemInfoEntry.Open()))
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            writer.WriteLine($"IcarusStarlink version: {version}");
            writer.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            writer.WriteLine($".NET runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
            writer.WriteLine($"Exported: {DateTimeOffset.Now:O}");
        }

        if (Directory.Exists(logsDirectory))
        {
            // *.log already matches both the regular icarusstarlink-*.log files and the
            // app.perf-*.log files. crash-*.txt is CrashReportWriter's own output (a .txt, not a
            // .log, specifically so it reads as a standalone document rather than another rolling
            // log — but that means it needs its own glob here, or it would silently never make it
            // into this zip at all).
            var logFiles = Directory.EnumerateFiles(logsDirectory, "*.log")
                .Concat(Directory.EnumerateFiles(logsDirectory, "crash-*.txt"));
            foreach (var logFile in logFiles)
            {
                try
                {
                    // Not archive.CreateEntryFromFile(logFile, ...) — that opens the source with a
                    // share mode too restrictive against Serilog's own currently-open handle on
                    // today's log file (the app usually still runs while exporting diagnostics,
                    // and that file is exactly the one most likely to matter for a bug just hit).
                    // FileShare.ReadWrite matches Serilog's own documented sharing so both can have
                    // the file open at once.
                    using var source = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var entry = archive.CreateEntry($"Logs/{Path.GetFileName(logFile)}");
                    using var entryStream = entry.Open();
                    source.CopyTo(entryStream);
                }
                catch (IOException)
                {
                    // One unreadable log file (locked by something other than this app's own
                    // Serilog sink, or deleted mid-enumeration) shouldn't sink the whole export —
                    // every other file, and settings.json below, are still worth having.
                }
            }
        }

        if (File.Exists(settingsFilePath))
        {
            var entry = archive.CreateEntry("settings.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(RedactSettings(File.ReadAllText(settingsFilePath)));
        }
    }

    private static string RedactSettings(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject settingsObject)
        {
            return json;
        }

        foreach (var propertyName in settingsObject.Select(kv => kv.Key).ToList())
        {
            if (SensitiveKeyMarkers.Any(marker => propertyName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                settingsObject[propertyName] = "[REDACTED]";
            }
        }

        return settingsObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
