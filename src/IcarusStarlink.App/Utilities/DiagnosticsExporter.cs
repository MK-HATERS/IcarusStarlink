using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// "Export diagnostics zip (logs + sanitized settings, no API keys/tokens)" per the spec — bundles
/// every rolled Serilog log file plus a redacted copy of settings.json into one zip a user can
/// attach to a bug report. Nothing this app stores in settings.json today is actually a secret (the
/// Nexus API key and FTP passwords both live in Windows Credential Manager via ICredentialStore,
/// never in this file) — the redaction pass below is defensive, in case a future settings field
/// ever holds something sensitive, not a fix for something currently exposed.
/// </summary>
public static class DiagnosticsExporter
{
    private static readonly string[] SensitiveKeyMarkers = ["key", "token", "password", "secret"];

    public static void Export(string logsDirectory, string settingsFilePath, string outputZipPath)
    {
        using var archive = ZipFile.Open(outputZipPath, ZipArchiveMode.Create);

        if (Directory.Exists(logsDirectory))
        {
            // *.log already matches both the regular icarusstarlink-*.log files and the
            // app.perf-*.log files — no need for a second, overlapping glob.
            foreach (var logFile in Directory.EnumerateFiles(logsDirectory, "*.log"))
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
