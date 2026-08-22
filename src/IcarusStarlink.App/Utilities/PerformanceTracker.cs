using System.Diagnostics;
using System.IO;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Optional, off-by-default operation-timing log ("app.perf" per the spec's own "Diagnostics &amp;
/// safety" section: "Performance tracking off by default (optional app.perf logging)"). A plain
/// text file, not routed through the app's own Serilog pipeline — this is meant to be a lightweight
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
                File.AppendAllText(path, line);
            }
        }
        catch (Exception)
        {
            // Best-effort diagnostic logging — a locked/permission-denied perf log file must never
            // be allowed to break the actual operation being timed.
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
