using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using IcarusStarlink.Core.Activity;
using Serilog;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// The one thing every crash path in App.xaml.cs (DispatcherUnhandledException,
/// AppDomain.UnhandledException, TaskScheduler.UnobservedTaskException, and the background
/// startup Task.Run) funnels into — a real gap this app had none of before: an unhandled
/// exception previously either hard-crashed with nothing logged, froze the splash screen forever
/// with no trace at all, or (for the many async [RelayCommand] methods Rebuild/Install/FTP/
/// downloads/save-editing are all built from) was silently discarded by CommunityToolkit.Mvvm's
/// own default "store and ignore" behavior — see FlowExceptionsToTaskScheduler on those attributes,
/// added alongside this so those exceptions actually reach the TaskScheduler.UnobservedTaskException
/// handler below instead of vanishing.
///
/// Writes a plain, immediately-flushed text file (never buffered — a crash report that's still
/// sitting in a buffer when the process dies a moment later is worthless) rather than relying
/// solely on the rolling Serilog log, so a single, easy-to-find, self-contained artifact exists
/// per crash — also still logged via Log.Fatal so it shows up in the regular log and gets swept
/// into DiagnosticsExporter's own zip automatically (DiagnosticsExporter globs "*.log", not
/// "crash-*.txt" specifically, so this file needs to be discoverable on its own; it's written
/// right next to the rolling logs for exactly that reason — a user attaching "everything in
/// Logs\" finds it without being told a special filename).
///
/// Never throws itself — a crash handler that crashes while handling a crash would be the one
/// truly unrecoverable failure mode here, so every step is best-effort.
/// </summary>
public static class CrashReportWriter
{
    /// <summary>
    /// Best-effort resolve of the app's own IActivityLog from the DI host, if one exists yet —
    /// null during a crash early enough in startup that the host was never built, or one late
    /// enough that _host has already been disposed. Passed in rather than resolved here so this
    /// class doesn't need to know about IHost/DI at all.
    /// </summary>
    public static void Write(string logsDirectory, Exception exception, string source, IActivityLog? activityLog)
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

        try
        {
            Directory.CreateDirectory(logsDirectory);
            var reportPath = Path.Combine(logsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

            using var writer = new StreamWriter(reportPath, append: false);
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
        }
        catch (Exception)
        {
            // Truly best-effort — if even this fails (disk full, no permissions), Log.Fatal above
            // was already attempted and there is nothing further this method can safely try.
        }
    }
}
