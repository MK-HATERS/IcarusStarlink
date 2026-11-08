using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Ensures only one IcarusStarlink process runs at a time — needed now that a real nxm:// "Mod
/// Manager Download" click launches this app via the OS, which by default would start a *second*
/// process rather than talk to the one already running. Standard, well-established .NET pattern:
/// a named Mutex for "am I the first instance", a named pipe for handing a second launch's own
/// command-line argument (the nxm:// URL) to the first instance before the second one exits.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    // The mutex is per Windows session already (no Global\ prefix), but pipe names are machine-wide:
    // with two users signed in, the second user's app couldn't create the pipe, and their links went
    // to the first user's app or nowhere. The session id keeps each user's apart.
    private const string MutexName = "IcarusStarlink-SingleInstance";
    private static readonly string PipeName = $"IcarusStarlink-NxmHandoff-{Process.GetCurrentProcess().SessionId}";

    private const int AllowAnyProcess = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Sent by a second launch that carries no nxm:// link — the running instance just brings its window to the front.</summary>
    public const string ActivateMessage = "activate";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _listenerCts;

    public SingleInstanceService() : this(MutexName, PipeName)
    {
    }

    /// <summary>Tests pass their own unique names so they never reach a real running Starlink.</summary>
    internal SingleInstanceService(string mutexName, string pipeName)
    {
        _pipeName = pipeName;
        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        IsFirstInstance = createdNew;
    }

    public bool IsFirstInstance { get; }

    /// <summary>How long a connected client gets to send its line before the listener drops it and waits for the next one.</summary>
    internal TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

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
                    // CurrentUserOnly: only this Windows account can connect (nxm:// links carry a
                    // download key). Asynchronous: the read timeout and the shutdown cancel end real
                    // overlapped I/O instead of a blocking call on a pool thread.
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);

                    // A client that connects but never writes (crashes between Connect and
                    // WriteLine, gets paused by a debugger/AV) would otherwise block this read
                    // forever — since maxNumberOfServerInstances is 1, that wedges every later
                    // nxm:// handoff for the rest of this app's life, since no new server could ever
                    // be created to accept them.
                    using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readTimeoutCts.CancelAfter(ReadTimeout);
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
                    // listener loop for every future launch — just go around and wait again. With
                    // a short back-off: a PERSISTENT failure (the pipe name already owned by another
                    // Windows user's session of this app on the same PC) used to spin this loop at
                    // 100% of a core for the app's whole lifetime.
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down — the while condition ends the loop.
                    }
                }
            }
        }, token);
    }

    /// <summary>Call only when IsFirstInstance is false. Best-effort: if the already-running instance genuinely can't be reached, there's nothing more this about-to-exit process can usefully do instead.</summary>
    // The 8 s connect wait is generous even though the first instance starts listening right at
    // launch (before its own DI host build/window show): a cold Debug-build start (JIT warm-up,
    // disk cache cold) can still take a few seconds, and waiting longer costs nothing, since this
    // process is about to exit either way.
    public static void SendToRunningInstance(string message) =>
        SendToRunningInstance(PipeName, message, connectTimeoutMs: 8000);

    internal static void SendToRunningInstance(string pipeName, string message, int connectTimeoutMs)
    {
        // Windows only lets the process the user just clicked bring a window to the front; this
        // passes that right on, or the running app's Activate() just flashes in the taskbar.
        AllowSetForegroundWindow(AllowAnyProcess);

        try
        {
            // CurrentUserOnly: refuse a pipe of this name that another Windows account created first,
            // so the link (and its download key) only ever goes to this account's Starlink.
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(connectTimeoutMs);
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
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Only the thread that created the mutex can release it. The app always disposes
                // on its UI thread; anything else lets Windows release it when the handle closes.
            }
        }

        _mutex.Dispose();
        _listenerCts?.Dispose();
    }
}
