using System.Windows.Threading;
using IcarusStarlink.Core.Activity;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Wraps the real, plain ActivityLog (IcarusStarlink.Core — deliberately dependency-free, so it
/// can't take an ILogger itself) so every entry also flows into Serilog, not just the in-memory,
/// never-persisted ObservableCollection. ActivityLog's own doc comment is explicit that its feed
/// doesn't survive a restart; before this, it didn't survive a CRASH either, which is exactly the
/// moment "what was the user doing right before this" matters most. Registered as IActivityLog in
/// place of the plain ActivityLog in App.xaml.cs — every real caller already goes through the
/// interface, so this is a transparent swap.
/// </summary>
/// <param name="uiDispatcher">
/// The window's thread. Entries is bound to the activity panel and the toasts, and background work
/// (downloads, merges, server automation) logs from other threads: changing a bound collection off
/// the UI thread throws or corrupts the view (review 2026-10-23), so an entry from another thread
/// is added there. Null (tests) adds it on the calling thread.
/// </param>
public sealed class LoggingActivityLog(ActivityLog inner, ILogger<LoggingActivityLog> logger, Dispatcher? uiDispatcher = null) : IActivityLog
{
    public System.Collections.ObjectModel.ObservableCollection<ActivityEntry> Entries => inner.Entries;

    public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info)
    {
        if (uiDispatcher is null || uiDispatcher.CheckAccess() || uiDispatcher.HasShutdownStarted)
        {
            inner.Log(message, kind);
        }
        else
        {
            uiDispatcher.BeginInvoke(() => inner.Log(message, kind));
        }

        switch (kind)
        {
            case ActivityEntryKind.Warning:
                logger.LogWarning("[Activity] {Message}", message);
                break;
            default:
                logger.LogInformation("[Activity] {Message}", message);
                break;
        }
    }
}
