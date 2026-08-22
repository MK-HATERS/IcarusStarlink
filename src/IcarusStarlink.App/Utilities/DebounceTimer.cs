using System.Windows.Threading;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Wraps a DispatcherTimer's "stop, then start on every change; run the action once nothing's
/// changed for `interval`" pattern, previously hand-rolled independently in LibraryItemViewModel
/// (notes save), NexusWatchlistItemViewModel (name save), LibraryViewModel and DownloadsViewModel
/// (both search) — same shape, four separate copies.
/// </summary>
public sealed class DebounceTimer
{
    private readonly DispatcherTimer _timer;
    private readonly Action _onElapsed;

    public DebounceTimer(TimeSpan interval, Action onElapsed)
    {
        _onElapsed = onElapsed;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            onElapsed();
        };
    }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Call on every change to the debounced value — restarts the countdown from zero rather than letting an in-flight one elapse early.</summary>
    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Stops a pending run without firing it — call before deleting/removing whatever onElapsed would otherwise write into, so a stale tick can't land on a folder name or list slot a later item reuses.</summary>
    public void Cancel() => _timer.Stop();

    /// <summary>
    /// If a run is pending, performs it immediately and stops the timer; a no-op otherwise. Call
    /// before evicting/replacing an object that still owns this timer but isn't being deleted (e.g.
    /// a cached row dropped by a search/filter reload) — Cancel() would silently discard whatever
    /// the user just typed, and leaving the timer running would let it fire later against an object
    /// nothing on screen still points at.
    /// </summary>
    public void Flush()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            _onElapsed();
        }
    }
}
