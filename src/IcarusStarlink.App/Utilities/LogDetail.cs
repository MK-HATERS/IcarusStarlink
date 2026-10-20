using System.Diagnostics;
using System.IO;
using Serilog.Core;
using Serilog.Events;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// How much the app's log file (Logs\icarusstarlink-&lt;date&gt;.log) records, switchable at runtime
/// from Settings › Diagnostics:
/// <list type="bullet">
/// <item><b>Basic</b> (default): what the user did and what happened (every Activity panel entry,
/// warnings and errors), without the web-request chatter that used to fill the file.</item>
/// <item><b>Advanced</b>: everything, including debug detail (each fix applied, the merge order and
/// picks, every web request) — for chasing a bug.</item>
/// </list>
/// </summary>
public static class LogDetail
{
    /// <summary>The app's own messages: Information in Basic, Debug in Advanced.</summary>
    public static LoggingLevelSwitch AppLevel { get; } = new(LogEventLevel.Information);

    /// <summary>Framework chatter (HTTP client, hosting): Warning in Basic, Debug in Advanced.</summary>
    public static LoggingLevelSwitch NoiseLevel { get; } = new(LogEventLevel.Warning);

    /// <summary>Where the log files are; set by the app at startup.</summary>
    public static string LogsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>One log file's size cap: past it the day's log rolls to a new file.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>How many log files are kept (oldest deleted first).</summary>
    public const int MaxFiles = 14;

    /// <summary>The Advanced log switches itself back to Basic after this long.</summary>
    public static readonly TimeSpan AdvancedFor = TimeSpan.FromHours(24);

    private static Timer? _switchBack;

    /// <summary>Raised (on the UI thread when there is one) when Advanced switched itself back to Basic, so Settings can show it.</summary>
    public static event Action? SwitchedBackToBasic;

    /// <summary>
    /// At startup and whenever the switch is turned on: Advanced only while its 24 hours last. Once
    /// past (at startup, or while the app stays open), it's switched off in the settings too, so the
    /// switch shows Basic again.
    /// </summary>
    public static void ApplyFromSettings(IcarusStarlink.Core.Settings.ISettingsService settings)
    {
        var current = settings.Current;
        if (current.AdvancedLogging && (current.AdvancedLoggingUntilUtc ?? DateTimeOffset.MinValue) <= DateTimeOffset.UtcNow)
        {
            SwitchBack(settings);
            return;
        }

        Apply(current.AdvancedLogging);
        _switchBack?.Dispose();
        _switchBack = null;
        if (current.AdvancedLogging && current.AdvancedLoggingUntilUtc is { } until)
        {
            var due = until - DateTimeOffset.UtcNow;
            _switchBack = new Timer(_ => RunOnUi(() => SwitchBack(settings)), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }
    }

    private static void SwitchBack(IcarusStarlink.Core.Settings.ISettingsService settings)
    {
        settings.Current.AdvancedLogging = false;
        settings.Current.AdvancedLoggingUntilUtc = null;
        settings.Save();
        Apply(false);
        Serilog.Log.Information("Advanced log ran for {Hours} hours and switched back to Basic.", AdvancedFor.TotalHours);
        SwitchedBackToBasic?.Invoke();
    }

    private static void RunOnUi(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private static void Apply(bool advanced)
    {
        AppLevel.MinimumLevel = advanced ? LogEventLevel.Debug : LogEventLevel.Information;
        NoiseLevel.MinimumLevel = advanced ? LogEventLevel.Debug : LogEventLevel.Warning;
        Serilog.Log.Information("Log detail: {Detail}", advanced ? "Advanced" : "Basic");
    }

    /// <summary>Opens the log folder in Explorer (creating it first if no log was written yet).</summary>
    public static void OpenFolder()
    {
        Directory.CreateDirectory(LogsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogsDirectory}\"") { UseShellExecute = true });
    }
}
