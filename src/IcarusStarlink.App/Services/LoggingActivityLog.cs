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
public sealed class LoggingActivityLog(ActivityLog inner, ILogger<LoggingActivityLog> logger) : IActivityLog
{
    public System.Collections.ObjectModel.ObservableCollection<ActivityEntry> Entries => inner.Entries;

    public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info)
    {
        inner.Log(message, kind);

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
