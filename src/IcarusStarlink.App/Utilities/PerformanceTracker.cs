using System.Diagnostics;
using System.IO;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Optional, off-by-default operation-timing log (app.perf). A plain text file, not routed through the app's own Serilog pipeline — this is meant to be a lightweight
/// diagnostic a user can toggle on/off for one session while chasing a slowdown, not part of the
/// app's regular structured logging.
/// </summary>
public sealed class PerformanceTracker(ISettingsService settingsService, string logsDirectory)
{
    private static readonly Lock WriteLock = new();

    /// <summary>Wrap the operation to time in a `using` block — recorded only if the setting is on at the moment Track() is called; toggling it mid-operation doesn't retroactively start/stop timing.</summary>
    public IDisposable Track(string operationName) =>
        settingsService.Current.PerformanceTrackingEnabled ? new Scope(this, operationName) : NullScope.Instance;

    private void Record(string operationName, TimeSpan elapsed)
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            var line = $"{DateTimeOffset.Now:O}  {operationName}  {elapsed.TotalMilliseconds:F0}ms{Environment.NewLine}";
            var path = Path.Combine(logsDirectory, $"app.perf-{DateTime.Now:yyyyMMdd}.log");
            lock (WriteLock)
            {
                var firstToday = !File.Exists(path);
                File.AppendAllText(path, line);
                if (firstToday)
                {
                    DeleteOldLogs();
                }
            }
        }
        catch (Exception)
        {
            // Best-effort diagnostic logging — a locked/permission-denied perf log file must never
            // be allowed to break the actual operation being timed.
        }
    }

    /// <summary>Keeps the newest <see cref="LogDetail.MaxFiles"/> daily timing logs (the yyyyMMdd stamp sorts by date); each delete is best effort.</summary>
    private void DeleteOldLogs()
    {
        var old = new DirectoryInfo(logsDirectory).EnumerateFiles("app.perf-*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(LogDetail.MaxFiles)
            .ToList();
        foreach (var file in old)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again on the first line of the next day.
            }
        }
    }

    private sealed class Scope(PerformanceTracker tracker, string operationName) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public void Dispose() => tracker.Record(operationName, _stopwatch.Elapsed);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
