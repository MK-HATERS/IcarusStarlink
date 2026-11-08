namespace IcarusStarlink.App.Services;

/// <summary>
/// What the Server page tells the main window: a server needs the host (crash restarts used up,
/// a failed update, backup, stop or start), and whether servers rely on Starlink staying open
/// (so closing the window can keep Starlink in the tray instead).
/// </summary>
public interface IHostNotifier
{
    /// <summary>Flashes Starlink's taskbar button, or shows a note by the clock while Starlink is in the tray. Never throws.</summary>
    void NeedsAttention(string message);

    /// <summary>The Server page hands over its "a server runs or automation is on" check once, when it is built.</summary>
    void WatchServers(Func<bool> serversNeedStarlink);
}

/// <summary>Does nothing: tests, and builds without a main window.</summary>
public sealed class NullHostNotifier : IHostNotifier
{
    public static readonly NullHostNotifier Instance = new();

    public void NeedsAttention(string message)
    {
    }

    public void WatchServers(Func<bool> serversNeedStarlink)
    {
    }
}
