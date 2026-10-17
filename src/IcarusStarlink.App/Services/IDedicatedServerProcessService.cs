using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Services;

/// <summary>A server process this app found running, from one install folder.</summary>
public sealed record RunningServer(int ProcessId, DateTime StartTime);

/// <summary>Starts, finds and stops dedicated server processes. An interface so the view model's tests never launch anything.</summary>
public interface IDedicatedServerProcessService
{
    /// <summary>The server process running from this install (and, when several profiles share it, the one using this Saved suffix if that can be told), or null.</summary>
    RunningServer? FindRunning(DedicatedServerProfile profile);

    /// <summary>Launches IcarusServer.exe with the profile's arguments in its own console window. Throws when the exe is missing.</summary>
    void Start(DedicatedServerProfile profile);

    /// <summary>
    /// Asks the server to shut down the way a person pressing Ctrl+C in its console would (Unreal
    /// treats that as a clean exit; the game saves then only if SaveGameOnExit is on), waits up to <paramref name="timeout"/>,
    /// then kills it. Returns true when it exited cleanly, false when it had to be killed.
    /// </summary>
    Task<bool> StopAsync(RunningServer server, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>The process's working set in bytes (what Task Manager calls memory), or null when it is gone or can't be read.</summary>
    long? GetMemoryBytes(int processId);

    /// <summary>Minimizes the server's console window. False while it has no console yet (the launcher, or an engine still starting) — try again later.</summary>
    bool TryMinimizeConsole(int processId);
}
