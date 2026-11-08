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
/// Credential Manager, never in settings.json, so that redaction is defensive. With a
/// DiagnosticsBugReport it also holds mod-setup.txt, last-game-crash.txt (a few named fields of the
/// newest CrashContext, never its ids or the .dmp) and ue4ss-log-tail.txt.
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

    /// <summary>How many lines from the end of UE4SS.log go in ue4ss-log-tail.txt.</summary>
    public const int Ue4ssLogTailLines = 300;

    /// <param name="bugReport">With it, the zip also gets mod-setup.txt, last-game-crash.txt and ue4ss-log-tail.txt (see DiagnosticsBugReport).</param>
    public static void Export(string logsDirectory, string settingsFilePath, string outputZipPath, DiagnosticsBugReport? bugReport = null)
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
                if (bugReport is not null)
                {
                    WriteBugReportEntries(archive, bugReport);
                }
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

    /// <summary>
    /// The three bug-report entries. Each is plain text with the user folder hidden, and one that
    /// can't be read says why in its own entry instead of stopping the export. Nothing else is taken
    /// from the game's crash folder: no minidump (.dmp), no CrashContext ids, no saves.
    /// </summary>
    private static void WriteBugReportEntries(ZipArchive archive, DiagnosticsBugReport bugReport)
    {
        var userFolder = UserFolderPattern(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        WriteTextEntry(archive, "mod-setup.txt", userFolder, () =>
            bugReport.ModSetup is { } modSetup ? modSetup() : "The mod setup is not available in this export.");
        WriteTextEntry(archive, "last-game-crash.txt", userFolder, () => DescribeLastCrash(bugReport.GameCrashesFolder));
        WriteTextEntry(archive, "ue4ss-log-tail.txt", userFolder, () => DescribeUe4ssLogTail(bugReport.IcarusContentPath));
    }

    private static void WriteTextEntry(ZipArchive archive, string name, Regex? userFolder, Func<string> read)
    {
        string text;
        try
        {
            text = read();
        }
        catch (Exception ex)
        {
            text = $"{name} couldn't be made: {ex.Message}";
        }

        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(HideUserFolder(text, userFolder));
    }

    private static string DescribeLastCrash(string? crashesFolder)
    {
        if (IcarusStarlink.Core.GameHealth.GameSessionHealthCheck.ReadLastCrash(crashesFolder) is not { } crash)
        {
            return "No game crash found in the game's crash folder.";
        }

        var text = new StringBuilder();
        text.AppendLine($"Last game crash: {crash.OccurredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({crash.CrashId})");
        text.AppendLine($"Crash type: {crash.CrashType ?? "unknown"}");
        text.AppendLine($"Error: {crash.ErrorMessage}");
        text.AppendLine($"UE4SS in the crash stack: {YesNo(crash.Ue4ssInStack)}");
        text.AppendLine($"Modules in the crash stack: {(crash.Modules.Count == 0 ? "none recorded" : string.Join(", ", crash.Modules))}");
        text.AppendLine($"Engine version: {crash.EngineVersion ?? "unknown"}");
        if (crash.SecondsSinceStart is { } seconds)
        {
            text.AppendLine($"Game had run for: {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}");
        }

        if (crash.GameStateName is { } state)
        {
            text.AppendLine($"Game state: {state}");
        }

        return text.ToString();
    }

    private static string DescribeUe4ssLogTail(string? icarusContentPath)
    {
        if (string.IsNullOrWhiteSpace(icarusContentPath))
        {
            return "No UE4SS.log found: the game folder is not set.";
        }

        var lines = IcarusStarlink.Core.GameHealth.GameSessionHealthCheck.ReadUe4ssLogTail(icarusContentPath, Ue4ssLogTailLines);
        if (lines.Count == 0)
        {
            return "No UE4SS.log found (UE4SS isn't installed, or the game hasn't run with it yet).";
        }

        var written = File.GetLastWriteTime(IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveLoaderLogPath(icarusContentPath));
        return $"The last {lines.Count} lines of UE4SS.log (last written {written:yyyy-MM-dd HH:mm}):{Environment.NewLine}"
               + string.Join(Environment.NewLine, lines) + Environment.NewLine;
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

/// <summary>
/// The bug-report half of a diagnostics zip: what a mod bug report needs that the app's own logs
/// don't hold.
/// </summary>
/// <param name="ModSetup">Writes mod-setup.txt (ModSetupReport); called off the UI thread. Null: the entry says it isn't available.</param>
/// <param name="GameCrashesFolder">Where last-game-crash.txt is read from; null is the game's own (%LocalAppData%\Icarus\Saved\Crashes).</param>
/// <param name="IcarusContentPath">The game's Content folder, for ue4ss-log-tail.txt; null when it isn't set.</param>
public sealed record DiagnosticsBugReport(Func<string>? ModSetup, string? GameCrashesFolder, string? IcarusContentPath);
