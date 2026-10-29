using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Services;

/// <summary>
/// See ISteamCmdService. Uses the steamcmd.exe the user pointed at (AppSettings.SteamCmdExePath)
/// when it exists, otherwise Starlink's own copy in a SteamCMD folder in Starlink's own folder —
/// downloaded from Valve's official URL only when the user asks for it. Runs hidden with its output
/// streamed line by line so the Server page can show progress.
/// </summary>
public sealed class SteamCmdService(HttpClient httpClient, Core.Settings.ISettingsService settings, string appRoot) : ISteamCmdService
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public string OwnInstallFolder => Path.Combine(appRoot, "SteamCMD");

    private string OwnExe => Path.Combine(OwnInstallFolder, "steamcmd.exe");

    public string? CurrentExePath =>
        settings.Current.SteamCmdExePath is { Length: > 0 } chosen && File.Exists(chosen) ? chosen
        : File.Exists(OwnExe) ? OwnExe
        : null;

    private string RequireExe() =>
        CurrentExePath ?? throw new InvalidOperationException("SteamCMD isn't set up — install it or point Starlink at your steamcmd.exe on the Server page.");

    public async Task InstallOwnCopyAsync(Action<string> onLine, CancellationToken cancellationToken = default)
    {
        onLine("[Starlink] Downloading SteamCMD from Valve…");
        Directory.CreateDirectory(OwnInstallFolder);
        var zipPath = Path.Combine(OwnInstallFolder, "steamcmd.zip");
        await HttpFileDownloader.DownloadToFileAsync(httpClient, [SteamCmd.WindowsDownloadUrl], zipPath, HttpFileDownloader.DefaultIdleTimeout, cancellationToken);

        ZipFile.ExtractToDirectory(zipPath, OwnInstallFolder, overwriteFiles: true);
        File.Delete(zipPath);

        // The first run only updates SteamCMD itself (and can exit with a non-zero code doing so).
        onLine("[Starlink] Letting SteamCMD update itself (first run only)…");
        await RunAsync(OwnExe, "+quit", onLine, cancellationToken);
    }

    public async Task<SteamCmdResult> InstallOrUpdateAsync(string installFolder, string branch, Action<string> onLine, CancellationToken cancellationToken = default)
    {
        var exe = RequireExe();
        Directory.CreateDirectory(installFolder);

        string? lastError = null;
        var sawSuccess = false;
        var exitCode = await RunAsync(exe, SteamCmd.BuildInstallArguments(installFolder, branch), line =>
        {
            if (SteamCmd.IsErrorLine(line))
            {
                lastError = line.Trim();
            }

            sawSuccess |= SteamCmd.IsSuccessLine(line);
            onLine(line);
        }, cancellationToken);

        var exeThere = File.Exists(DedicatedServerLayout.LauncherPath(installFolder));
        if (sawSuccess && exeThere)
        {
            return new SteamCmdResult(true, $"Server installed/updated on the {SteamCmd.NormalizeBranch(branch)} branch.");
        }

        return new SteamCmdResult(false, lastError ?? (exeThere
            ? $"SteamCMD finished (exit code {exitCode}) without confirming success — check the output above."
            : $"SteamCMD finished (exit code {exitCode}) but IcarusServer.exe isn't in '{installFolder}'."));
    }

    public async Task<int?> GetLatestBuildIdAsync(string branch, CancellationToken cancellationToken = default)
    {
        var exe = RequireExe();
        var output = new StringBuilder();
        await RunAsync(exe, SteamCmd.BuildAppInfoArguments(), line => output.AppendLine(line), cancellationToken);
        return SteamCmd.ParseLatestBuildId(output.ToString(), branch);
    }

    /// <summary>Runs steamcmd.exe hidden; every line (split on \r or \n — progress lines rewrite in place) goes to onLine. One run at a time: SteamCMD locks its own folder.</summary>
    private async Task<int> RunAsync(string exe, string arguments, Action<string> onLine, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };
            // SteamCMD holds back its output when it isn't writing to a real console — a real run
            // showed nothing on stdout for minutes while its own logs\console_log.txt was printing
            // "Update state (0x61) downloading, progress: 40.08 …" every two seconds. So the log file
            // is followed live too, and a line already seen from one source isn't repeated from the other.
            var consoleLog = new LogTailer(Path.Combine(Path.GetDirectoryName(exe)!, "logs", "console_log.txt"));
            consoleLog.SeekToEnd();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var gate = new object();
            void Forward(string line)
            {
                var text = SteamCmd.StripLogTimestamp(line).Trim();
                if (text.Length == 0)
                {
                    return;
                }

                lock (gate)
                {
                    if (!seen.Add(text))
                    {
                        return;
                    }

                    if (seen.Count > 2000)
                    {
                        seen.Clear();
                    }
                }

                onLine(text);
            }

            process.Start();
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already exiting (or access denied as it goes): a throw here would come out
                    // of Cancel() on the UI thread, in the Cancel button's own command.
                }
            });

            using var stopFollowing = new CancellationTokenSource();
            var follow = Task.Run(async () =>
            {
                while (!stopFollowing.IsCancellationRequested)
                {
                    foreach (var line in consoleLog.Poll())
                    {
                        Forward(line);
                    }

                    try
                    {
                        await Task.Delay(1000, stopFollowing.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            });

            var stdout = PumpAsync(process.StandardOutput, Forward);
            var stderr = PumpAsync(process.StandardError, Forward);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(stdout, stderr);
            }
            finally
            {
                stopFollowing.Cancel();
                await follow;
                foreach (var line in consoleLog.Poll())
                {
                    Forward(line);
                }
            }

            return process.ExitCode;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var current = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (current.Length > 0)
                    {
                        onLine(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        if (current.Length > 0)
        {
            onLine(current.ToString());
        }
    }
}
