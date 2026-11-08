using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Storage.Server;

namespace IcarusStarlink.Storage.Tests.Server;

/// <summary>
/// The real FluentFTP client against a small FTP server on this machine that, like
/// SurvivalServers, refuses every DELE but accepts a plain STOR over an existing file and a
/// rename. Replacing a file used to send DELE first (FluentFTP's Overwrite mode), so every
/// second install of the merged pak, and every mods.txt rename, failed on that host.
/// </summary>
public sealed class FluentFtpClientTests
{
    private static FtpSiteProfile SiteFor(LoopbackFtpServer server) => new()
    {
        Id = Guid.NewGuid(), Name = "t", Host = "127.0.0.1", Port = server.Port, Username = "u",
    };

    [Fact]
    public async Task UploadFile_OverAnExistingFile_WritesInPlace_WithoutDeletingFirst()
    {
        await using var server = new LoopbackFtpServer();
        server.Files["/mods/ISL-Merged_P.pak"] = Encoding.ASCII.GetBytes("old");
        var local = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "ISL-Merged_P.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "new");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var client = new FluentFtpClient();
            await client.ConnectAsync(SiteFor(server), "p", timeout.Token);

            await client.UploadFileAsync(local, "mods/ISL-Merged_P.pak", timeout.Token);

            Assert.Equal("new", Encoding.ASCII.GetString(server.Files["/mods/ISL-Merged_P.pak"]));
            Assert.DoesNotContain(server.Commands, c => c.StartsWith("DELE", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(local)!, recursive: true);
        }
    }

    [Fact]
    public async Task RenameFile_OntoAnExistingName_IsAPlainRename_WithoutDeletingFirst()
    {
        await using var server = new LoopbackFtpServer();
        server.Files["/mods/mods.txt"] = Encoding.ASCII.GetBytes("old list");
        server.Files["/mods/mods.txt.uploading"] = Encoding.ASCII.GetBytes("new list");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new FluentFtpClient();
        await client.ConnectAsync(SiteFor(server), "p", timeout.Token);

        await client.RenameFileAsync("mods/mods.txt.uploading", "mods/mods.txt", timeout.Token);

        Assert.Equal("new list", Encoding.ASCII.GetString(server.Files["/mods/mods.txt"]));
        Assert.False(server.Files.ContainsKey("/mods/mods.txt.uploading"));
        Assert.Contains(server.Commands, c => c.StartsWith("RNFR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.Commands, c => c.StartsWith("RNTO", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("DELE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A minimal FTP server on a free loopback port: one control connection at a time, passive
    /// data connections, files kept in memory by absolute path. Every DELE is refused the way a
    /// delete-blocked host answers ("550 Permission denied"). Records each command it receives.
    /// </summary>
    private sealed class LoopbackFtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentBag<Task> _sessions = [];

        public LoopbackFtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<string> Commands { get; } = new();

        private readonly ConcurrentDictionary<string, bool> _madeFolders = new(StringComparer.Ordinal);

        private bool FolderExists(string path) =>
            path == "/" || _madeFolders.ContainsKey(path) || Files.Keys.Any(f => f.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal));

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _sessions.Add(Task.Run(() => ServeAsync(client)));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private static string Absolute(string cwd, string path)
        {
            var full = path.StartsWith('/') ? path : cwd.TrimEnd('/') + "/" + path;
            return full.Length > 1 ? full.TrimEnd('/') : full;
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            TcpListener? passive = null;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 Loopback test server");
                var cwd = "/";
                string? renameFrom = null;
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    Commands.Enqueue(line);
                    var space = line.IndexOf(' ');
                    var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var arg = space < 0 ? "" : line[(space + 1)..];
                    switch (verb)
                    {
                        case "USER":
                            await writer.WriteLineAsync("331 Password please.");
                            break;
                        case "PASS":
                            await writer.WriteLineAsync("230 Logged in.");
                            break;
                        case "SYST":
                            await writer.WriteLineAsync("215 UNIX Type: L8");
                            break;
                        case "FEAT":
                            await writer.WriteLineAsync("211-Features:");
                            await writer.WriteLineAsync(" SIZE");
                            await writer.WriteLineAsync(" EPSV");
                            await writer.WriteLineAsync("211 End");
                            break;
                        case "OPTS":
                        case "TYPE":
                        case "NOOP":
                            await writer.WriteLineAsync("200 OK.");
                            break;
                        case "PWD":
                            await writer.WriteLineAsync($"257 \"{cwd}\" is the current directory.");
                            break;
                        case "CWD":
                            var folder = Absolute(cwd, arg);
                            if (FolderExists(folder))
                            {
                                cwd = folder;
                                await writer.WriteLineAsync("250 Directory changed.");
                            }
                            else
                            {
                                await writer.WriteLineAsync("550 No such directory.");
                            }

                            break;
                        case "MKD":
                            _madeFolders[Absolute(cwd, arg)] = true;
                            await writer.WriteLineAsync($"257 \"{Absolute(cwd, arg)}\" created.");
                            break;
                        case "SIZE":
                            await writer.WriteLineAsync(Files.TryGetValue(Absolute(cwd, arg), out var sized) ? $"213 {sized.Length}" : "550 File not found.");
                            break;
                        case "EPSV":
                            passive?.Stop();
                            passive = new TcpListener(IPAddress.Loopback, 0);
                            passive.Start();
                            await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{((IPEndPoint)passive.LocalEndpoint).Port}|)");
                            break;
                        case "PASV":
                            passive?.Stop();
                            passive = new TcpListener(IPAddress.Loopback, 0);
                            passive.Start();
                            var port = ((IPEndPoint)passive.LocalEndpoint).Port;
                            await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                            break;
                        case "STOR":
                            if (passive is null)
                            {
                                await writer.WriteLineAsync("425 Use EPSV or PASV first.");
                                break;
                            }

                            await writer.WriteLineAsync("150 Ok to send data.");
                            using (var data = await passive.AcceptTcpClientAsync(_stop.Token))
                            {
                                using var received = new MemoryStream();
                                await data.GetStream().CopyToAsync(received, _stop.Token);
                                Files[Absolute(cwd, arg)] = received.ToArray();
                            }

                            passive.Stop();
                            passive = null;
                            await writer.WriteLineAsync("226 Transfer complete.");
                            break;
                        case "RNFR":
                            var from = Absolute(cwd, arg);
                            if (Files.ContainsKey(from))
                            {
                                renameFrom = from;
                                await writer.WriteLineAsync("350 Ready for RNTO.");
                            }
                            else
                            {
                                await writer.WriteLineAsync("550 No such file.");
                            }

                            break;
                        case "RNTO":
                            if (renameFrom is not null && Files.TryRemove(renameFrom, out var moved))
                            {
                                Files[Absolute(cwd, arg)] = moved;
                                renameFrom = null;
                                await writer.WriteLineAsync("250 Rename successful.");
                            }
                            else
                            {
                                await writer.WriteLineAsync("503 RNFR first.");
                            }

                            break;
                        case "DELE":
                            await writer.WriteLineAsync("550 Permission denied.");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 Goodbye.");
                            return;
                        default:
                            await writer.WriteLineAsync("502 Command not implemented.");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                passive?.Stop();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _acceptLoop;
            await Task.WhenAll(_sessions);
            _stop.Dispose();
        }
    }
}
