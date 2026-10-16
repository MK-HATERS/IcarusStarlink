using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Services;

/// <summary>
/// See ISteamCmdService. SteamCMD lives in this app's own Tools\steamcmd folder (portable, like
/// UnrealPak), is downloaded from Valve's official URL on first use, and runs hidden with its
/// output streamed line by line so the Server page can show progress.
/// </summary>
public sealed class SteamCmdService(HttpClient httpClient, string toolsDirectory) : ISteamCmdService
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public string SteamCmdFolder => Path.Combine(toolsDirectory, "steamcmd");

    public string SteamCmdExe => Path.Combine(SteamCmdFolder, "steamcmd.exe");

    public async Task EnsureInstalledAsync(Action<string> onLine, CancellationToken cancellationToken = default)
    {
        if (File.Exists(SteamCmdExe))
        {
            return;
        }

        onLine("[Starlink] Downloading SteamCMD from Valve…");
        Directory.CreateDirectory(SteamCmdFolder);
        var zipPath = Path.Combine(SteamCmdFolder, "steamcmd.zip");
        using (var response = await httpClient.GetAsync(SteamCmd.WindowsDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(zipPath);
            await response.Content.CopyToAsync(file, cancellationToken);
        }

        ZipFile.ExtractToDirectory(zipPath, SteamCmdFolder, overwriteFiles: true);
        File.Delete(zipPath);

        // The first run only updates SteamCMD itself (and can exit with a non-zero code doing so).
        onLine("[Starlink] Letting SteamCMD update itself (first run only)…");
        await RunAsync("+quit", onLine, cancellationToken);
    }

    public async Task<SteamCmdResult> InstallOrUpdateAsync(string installFolder, string branch, Action<string> onLine, CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(onLine, cancellationToken);
        Directory.CreateDirectory(installFolder);

        string? lastError = null;
        var sawSuccess = false;
        var exitCode = await RunAsync(SteamCmd.BuildInstallArguments(installFolder, branch), line =>
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
        await EnsureInstalledAsync(_ => { }, cancellationToken);
        var output = new StringBuilder();
        await RunAsync(SteamCmd.BuildAppInfoArguments(), line => output.AppendLine(line), cancellationToken);
        return SteamCmd.ParseLatestBuildId(output.ToString(), branch);
    }

    /// <summary>Runs steamcmd.exe hidden; every line (split on \r or \n — progress lines rewrite in place) goes to onLine. One run at a time: SteamCMD locks its own folder.</summary>
    private async Task<int> RunAsync(string arguments, Action<string> onLine, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = SteamCmdExe,
                    Arguments = arguments,
                    WorkingDirectory = SteamCmdFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };
            process.Start();
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            });

            var stdout = PumpAsync(process.StandardOutput, onLine);
            var stderr = PumpAsync(process.StandardError, onLine);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
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
