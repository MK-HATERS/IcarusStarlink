using System.IO;
using System.IO.Pipes;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Ensures only one IcarusStarlink process runs at a time — needed now that a real nxm:// "Mod
/// Manager Download" click launches this app via the OS, which by default would start a *second*
/// process rather than talk to the one already running. Standard, well-established .NET pattern
/// (confirmed against current guidance during Phase 8.3c planning, not assumed): a named Mutex for
/// "am I the first instance", a named pipe for handing a second launch's own command-line argument
/// (the nxm:// URL) to the first instance before the second one exits.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "IcarusStarlink-SingleInstance";
    private const string PipeName = "IcarusStarlink-NxmHandoff";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listenerCts;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
    }

    public bool IsFirstInstance { get; }

    /// <summary>Only meaningful when IsFirstInstance is true. onMessageReceived runs on a background thread — the caller must marshal back to the UI thread itself.</summary>
    public void StartListening(Action<string> onMessageReceived)
    {
        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);
                    await server.WaitForConnectionAsync(token);

                    // A client that connects but never writes (crashes between Connect and
                    // WriteLine, gets paused by a debugger/AV) would otherwise block this read
                    // forever — since maxNumberOfServerInstances is 1, that wedges every later
                    // nxm:// handoff for the rest of this app's life, since no new server could ever
                    // be created to accept them.
                    using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync(readTimeoutCts.Token);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        onMessageReceived(message);
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // A single connection's own transient failure shouldn't kill the whole
                    // listener loop for every future launch — just go around and wait again.
                }
            }
        }, token);
    }

    /// <summary>Call only when IsFirstInstance is false. Best-effort: if the already-running instance genuinely can't be reached, there's nothing more this about-to-exit process can usefully do instead.</summary>
    public static void SendToRunningInstance(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // Generous even though the first instance now starts listening immediately on launch
            // (before its own DI host build/window show) — a genuinely cold Debug-build start
            // (JIT warm-up, disk cache cold) can still take a few seconds, and there's no real
            // downside to waiting longer here: this process is about to exit either way.
            client.Connect(timeout: 8000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(message);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        if (IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _listenerCts?.Dispose();
    }
}
