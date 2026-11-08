using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// "Export diagnostics zip": one zip a player can attach to a bug report. It holds
/// system-info.txt (app version, OS, .NET runtime), every *.log in the logs folder (the app logs,
/// with the session's activity, and the app.perf timing logs), every crash-*.txt from
/// CrashReportWriter, and a copy of settings.json with values under sensitive names (key, token,
/// password, secret) replaced by [REDACTED]. The Nexus API key and FTP passwords live in Windows
/// Credential Manager, never in settings.json, so that redaction is defensive.
///
/// The zip is often posted in public, so the player's Windows user folder (C:\Users\name) shows
/// as %USERPROFILE% in every text entry; the files on disk stay as they are.
/// </summary>
public static class DiagnosticsExporter
{
    private const string UserFolderStandIn = "%USERPROFILE%";

    /// <summary>JSON-escaped \\, a plain \, or /.</summary>
    private const string AnySeparator = @"(?:\\\\|\\|/)";

    private static readonly string[] SensitiveKeyMarkers = ["key", "token", "password", "secret"];

    public static void Export(string logsDirectory, string settingsFilePath, string outputZipPath)
    {
        // Written next to the target first and moved over it only once complete: the file the
        // user picked (and confirmed overwriting in the save dialog) is replaced only by a whole
        // zip, never deleted up front and left missing or half-written when the export fails.
        var tempZipPath = outputZipPath + ".tmp";
        try
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }

            using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                WriteEntries(archive, logsDirectory, settingsFilePath);
            }

            File.Move(tempZipPath, outputZipPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempZipPath);
            }
            catch (Exception)
            {
                // Best effort: the export's own error is the one worth reporting.
            }

            throw;
        }
    }

    private static void WriteEntries(ZipArchive archive, string logsDirectory, string settingsFilePath)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userFolder = UserFolderPattern(userProfile);

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
            if (userFolder is not null)
            {
                // The name is hidden, but a space or a non-English letter in it can break paths.
                var name = Path.GetFileName(userProfile.TrimEnd('\\', '/'));
                writer.WriteLine($"Windows user folder: shown as {UserFolderStandIn} in this zip " +
                                 $"(its name has spaces: {YesNo(name.Contains(' '))}; letters outside English: {YesNo(name.Any(c => c > 127))})");
            }
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
                    // Read as text (each file is at most a few MB) so the user folder can be hidden.
                    using var source = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(source);
                    var text = HideUserFolder(reader.ReadToEnd(), userFolder);
                    var entry = archive.CreateEntry($"Logs/{Path.GetFileName(logFile)}");
                    using var entryWriter = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    entryWriter.Write(text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unreadable log file (locked by something other than this app's own
                    // Serilog sink, or deleted mid-enumeration) shouldn't sink the whole export —
                    // every other file, and settings.json below, are still worth having.
                }
            }
        }

        if (File.Exists(settingsFilePath))
        {
            string settingsText;
            try
            {
                settingsText = RedactSettings(File.ReadAllText(settingsFilePath), userFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Same as an unreadable log: say so in the zip, keep everything else.
                settingsText = $"settings.json couldn't be read: {ex.Message}";
            }

            var entry = archive.CreateEntry("settings.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(settingsText);
        }
    }

    private static string RedactSettings(string json, Regex? userFolder)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // A corrupt settings.json is exactly the thing worth shipping in a diagnostics zip — it
            // used to abort the whole export instead. Nothing in it is secret (see the class doc),
            // so the raw text goes in with only the user folder hidden (the pattern also matches
            // the JSON-escaped \\).
            return HideUserFolder(json, userFolder);
        }

        if (parsed is not JsonObject settingsObject)
        {
            return HideUserFolder(json, userFolder);
        }

        Redact(settingsObject, userFolder);
        return settingsObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Every level, not just the top: a secret nested in a sub-object (or a list of them) is
    /// redacted too, and the user folder is hidden in every other string value.
    /// </summary>
    private static void Redact(JsonNode? node, Regex? userFolder)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var propertyName in obj.Select(kv => kv.Key).ToList())
                {
                    if (SensitiveKeyMarkers.Any(marker => propertyName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                    {
                        obj[propertyName] = "[REDACTED]";
                    }
                    else if (obj[propertyName] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[propertyName] = HideUserFolder(text, userFolder);
                    }
                    else
                    {
                        Redact(obj[propertyName], userFolder);
                    }
                }

                break;
            case JsonArray array:
                // By index, not foreach: a string value is replaced in place.
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = HideUserFolder(text, userFolder);
                    }
                    else
                    {
                        Redact(array[i], userFolder);
                    }
                }

                break;
        }
    }

    /// <summary>The user's own folder in any of its spellings; null when there's nothing worth hiding.</summary>
    private static Regex? UserFolderPattern(string userProfile)
    {
        var segments = userProfile.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        // Not followed by a letter or digit: C:\Users\Jim must not match inside C:\Users\Jimmy.
        var pattern = string.Join(AnySeparator, segments.Select(Regex.Escape)) + @"(?![\p{L}\p{N}_])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string HideUserFolder(string text, Regex? userFolder) =>
        userFolder is null ? text : userFolder.Replace(text, UserFolderStandIn);

    private static string YesNo(bool value) => value ? "yes" : "no";
}
