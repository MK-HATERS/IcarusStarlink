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
/// Two profiles on one install (different -saveddirsuffix) are told apart by the suffix on the
/// process's own command line, read with NtQueryInformationProcess(ProcessCommandLineInformation)
/// — the documented-since-Windows-8.1 way, needing only limited query rights. When it can't be read
/// (a server started as administrator) the process still counts, as it did before.
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
        RunningServer? unsure = null;
        foreach (var name in new[] { DedicatedServerLayout.ShippingProcessName, DedicatedServerLayout.LauncherProcessName })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var (exe, commandLine) = ReadImageAndCommandLine(process.Id);
                        exe ??= process.MainModule?.FileName;
                        if (exe is null || !exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!DedicatedServerCommandLine.IsThisProfilesProcess(commandLine, profile))
                        {
                            continue; // another profile's server on the same install
                        }

                        var found = new RunningServer(process.Id, process.StartTime);
                        if (commandLine is not null)
                        {
                            return found;
                        }

                        // Couldn't read its command line: use it only if nothing certain turns up.
                        unsure ??= found;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // Access denied (an elevated server) or it exited mid-check.
                    }
                }
            }

            if (unsure is not null)
            {
                return unsure;
            }
        }

        return null;
    }

    /// <summary>The exe path and the full command line of a process, each null when Windows won't say.</summary>
    private static (string? Exe, string? CommandLine) ReadImageAndCommandLine(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return (null, null);
        }

        try
        {
            string? exe = null;
            var pathBuffer = new char[32768];
            var size = (uint)pathBuffer.Length;
            if (QueryFullProcessImageName(handle, 0, pathBuffer, ref size))
            {
                exe = new string(pathBuffer, 0, (int)size);
            }

            return (exe, ReadCommandLine(handle));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? ReadCommandLine(IntPtr handle)
    {
        NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1 << 20)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
            {
                return null;
            }

            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

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
            // The PID may be minutes old (automation's last look): if that server has exited since,
            // Windows can have given its number to something else. Never touch a process that isn't
            // the one recorded — same name, same start time.
            try
            {
                if (!IsRecordedServer(process.ProcessName, process.StartTime, server))
                {
                    return true; // the recorded server is gone
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true; // exited while looking (or can't be read, so it isn't ours to stop)
            }

            // Off the UI thread: sending Ctrl+C waits for it to be delivered.
            if (!await Task.Run(() => TrySendCtrlC(server.ProcessId), cancellationToken))
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

    /// <summary>A process is the recorded server: one of the server's two exe names, started when the recorded one was (FindRunning took its start time from the process itself).</summary>
    internal static bool IsRecordedServer(string processName, DateTime startTime, RunningServer server) =>
        (string.Equals(processName, DedicatedServerLayout.ShippingProcessName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, DedicatedServerLayout.LauncherProcessName, StringComparison.OrdinalIgnoreCase))
        && (startTime - server.StartTime).Duration() < TimeSpan.FromSeconds(1);

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
