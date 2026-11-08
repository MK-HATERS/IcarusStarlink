using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using IcarusStarlink.Core.Activity;
using Serilog;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Writes a crash report for the crash handlers in App.xaml.cs (DispatcherUnhandledException,
/// AppDomain.UnhandledException, TaskScheduler.UnobservedTaskException and the background startup
/// task). The exception goes to the regular log through Log.Fatal and into its own
/// crash-yyyyMMdd-HHmmss-fff.txt next to the logs, written straight to disk and flushed so it
/// survives the process dying a moment later. DiagnosticsExporter puts every crash-*.txt in the
/// diagnostics zip.
///
/// Which handler sees a failure: an async [RelayCommand] WITHOUT FlowExceptionsToTaskScheduler
/// awaits its task and rethrows a failure on the UI thread, so it reaches
/// DispatcherUnhandledException (Starlink reports it and closes). WITH FlowExceptionsToTaskScheduler
/// = true the failure stays on the command's ExecutionTask, which nothing reads; it reaches
/// TaskScheduler.UnobservedTaskException only when that Task is garbage-collected, which can be
/// much later or never. AppDomain.UnhandledException covers a crash on another thread.
///
/// Keeps the newest <see cref="MaxReports"/> reports and deletes older ones, so a background task
/// that fails once per session doesn't add a file forever.
///
/// Never throws: a crash handler that crashes while handling a crash can't recover, so every step
/// is best effort.
/// </summary>
public static class CrashReportWriter
{
    /// <summary>How many crash-*.txt files are kept, the one just written included.</summary>
    public const int MaxReports = 20;

    /// <summary>
    /// Writes the report and returns its full path, or null when no report could be written (the
    /// folder can't be created, the disk is full, no permission), so a crash box can name the file
    /// or say there is none. <paramref name="activityLog"/> is the app's IActivityLog when the DI
    /// host exists (null during a crash early in startup or after the host was disposed); passed
    /// in so this class doesn't need to know about the host.
    /// </summary>
    public static string? Write(string logsDirectory, Exception exception, string source, IActivityLog? activityLog)
    {
        try
        {
            Log.Fatal(exception, "Unhandled exception ({Source})", source);
        }
        catch (Exception)
        {
            // Serilog itself might be what's broken (disk full, sink faulted) — the file write
            // below is the fallback, not contingent on this succeeding.
        }

        string? reportPath = null;
        var written = false;
        try
        {
            Directory.CreateDirectory(logsDirectory);
            // Milliseconds plus a counter: a second crash in the same second (a crash while handling
            // the first one is common) must not overwrite the first, which is usually the real one.
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            reportPath = Path.Combine(logsDirectory, $"crash-{stamp}.txt");
            for (var n = 2; File.Exists(reportPath); n++)
            {
                reportPath = Path.Combine(logsDirectory, $"crash-{stamp}-{n}.txt");
            }

            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

            using var writer = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write));
            writer.WriteLine("IcarusStarlink crash report");
            writer.WriteLine($"Timestamp: {DateTimeOffset.Now:O}");
            writer.WriteLine($"Source: {source}");
            writer.WriteLine($"App version: {version}");
            writer.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            writer.WriteLine($".NET runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
            writer.WriteLine($"Process bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            writer.WriteLine();
            writer.WriteLine("--- Exception ---");
            writer.WriteLine(exception.ToString());

            if (activityLog is not null)
            {
                writer.WriteLine();
                writer.WriteLine("--- Recent activity (newest first) ---");
                if (activityLog.Entries.Count == 0)
                {
                    writer.WriteLine("(none recorded this session)");
                }
                else
                {
                    foreach (var entry in activityLog.Entries)
                    {
                        writer.WriteLine($"[{entry.Timestamp:O}] {entry.Kind}: {entry.Message}");
                    }
                }
            }

            writer.Flush();
            written = true;
        }
        catch (Exception)
        {
            // Truly best-effort — if even this fails (disk full, no permissions), Log.Fatal above
            // was already attempted and there is nothing further this method can safely try.
        }

        try
        {
            DeleteOldReports(logsDirectory, reportPath);
        }
        catch (Exception)
        {
            // A cleanup failure must never cost the report.
        }

        return written ? reportPath : null;
    }

    /// <summary>Deletes all but the newest reports, never <paramref name="justWritten"/>; each delete is best effort.</summary>
    private static void DeleteOldReports(string logsDirectory, string? justWritten)
    {
        var old = new DirectoryInfo(logsDirectory).EnumerateFiles("crash-*.txt")
            .Where(f => !string.Equals(f.FullName, justWritten, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(MaxReports - 1)
            .ToList();
        foreach (var file in old)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again after the next report.
            }
        }
    }
}
