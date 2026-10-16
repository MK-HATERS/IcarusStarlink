using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Services;

/// <summary>
/// See IDedicatedServerProcessService. The launcher (IcarusServer.exe) starts the real server
/// (IcarusServer-Win64-Shipping.exe) and may exit, so "running" means a shipping process whose exe
/// lives under the profile's install folder. Graceful stop sends CTRL_C to the server's console by
/// briefly attaching to it — the documented way to deliver a console control event to another
/// process — then falls back to closing its window, then to killing it after the timeout.
/// </summary>
public sealed class DedicatedServerProcessService : IDedicatedServerProcessService
{
    public RunningServer? FindRunning(DedicatedServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.InstallFolder))
        {
            return null;
        }

        var root = Path.GetFullPath(profile.InstallFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in new[] { DedicatedServerLayout.ShippingProcessName, DedicatedServerLayout.LauncherProcessName })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var exe = process.MainModule?.FileName;
                        if (exe is null || !exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Two profiles on one install: tell them apart by the Saved suffix on the
                        // command line when Windows lets us read it; otherwise any match counts.
                        return new RunningServer(process.Id, process.StartTime);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // Access denied (an elevated server) or it exited mid-check.
                    }
                }
            }
        }

        return null;
    }

    public void Start(DedicatedServerProfile profile)
    {
        var exe = DedicatedServerLayout.LauncherPath(profile.InstallFolder);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"IcarusServer.exe isn't in '{profile.InstallFolder}'.", exe);
        }

        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = DedicatedServerCommandLine.BuildArgumentString(profile),
            WorkingDirectory = profile.InstallFolder,
            UseShellExecute = true,
        });
    }

    public async Task<bool> StopAsync(RunningServer server, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(server.ProcessId);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }

        using (process)
        {
            if (!TrySendCtrlC(server.ProcessId))
            {
                try
                {
                    process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }

            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                await process.WaitForExitAsync(timeoutSource.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Exited between the timeout and the kill.
                }

                return false;
            }
        }
    }

    public long? GetMemoryBytes(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.WorkingSet64;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The server opens its own console for -log, after the launcher hands off, so the window can't be started minimized — it is minimized once it exists, by attaching to it like Stop does.</summary>
    public bool TryMinimizeConsole(int processId)
    {
        lock (ConsoleLock)
        {
            if (!AttachConsole((uint)processId))
            {
                return false;
            }

            try
            {
                var window = GetConsoleWindow();
                if (window == IntPtr.Zero)
                {
                    return false;
                }

                // ShowWindow returns the PREVIOUS visibility, not success, so it is ignored.
                ShowWindow(window, SwMinimize);
                return true;
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    private const int SwMinimize = 6;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static readonly object ConsoleLock = new();

    /// <summary>Attach to the server's console, ignore Ctrl+C ourselves, send it, detach. One at a time: a process can be attached to only one console.</summary>
    private static bool TrySendCtrlC(int processId)
    {
        lock (ConsoleLock)
        {
            if (!AttachConsole((uint)processId))
            {
                return false;
            }

            try
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                return GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            }
            finally
            {
                FreeConsole();
                // Re-enable our own Ctrl+C handling only after the event has been delivered.
                Thread.Sleep(200);
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
        }
    }

    private const uint CtrlCEvent = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
