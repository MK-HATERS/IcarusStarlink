using FluentFTP;
using IcarusStarlink.Core.Server;
using CoreIFtpClient = IcarusStarlink.Core.Server.IFtpClient;

namespace IcarusStarlink.Storage.Server;

/// <summary>
/// Wraps FluentFTP's real AsyncFtpClient (confirmed API shape via its own source during Phase 8.5
/// planning, not guessed) — this is the only place in the app that references FluentFTP directly;
/// everything else talks to the Core-level IFtpClient/FtpEntry types instead. One instance = one
/// connection, matching AsyncFtpClient's own lifecycle (construct, Connect, use, Disconnect/Dispose).
/// </summary>
public sealed class FluentFtpClient : CoreIFtpClient
{
    private AsyncFtpClient? _client;

    public async Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default)
    {
        _client = new AsyncFtpClient(site.Host, site.Username, password, site.Port);
        _client.Config.EncryptionMode = site.EncryptionMode switch
        {
            IcarusStarlink.Core.Server.FtpEncryptionMode.Explicit => FluentFTP.FtpEncryptionMode.Explicit,
            IcarusStarlink.Core.Server.FtpEncryptionMode.Implicit => FluentFTP.FtpEncryptionMode.Implicit,
            _ => FluentFTP.FtpEncryptionMode.None,
        };

        await _client.Connect(cancellationToken);
    }

    public async Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var items = await RequireClient().GetListing(remotePath, cancellationToken);
        return [.. items.Select(item => new FtpEntry(
            item.Name,
            item.Type == FtpObjectType.Directory,
            item.Size >= 0 ? item.Size : null,
            item.Modified == DateTime.MinValue ? null : new DateTimeOffset(item.Modified)))];
    }

    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
        await RequireClient().UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken);

    public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
        await RequireClient().DownloadFile(localPath, remotePath, FtpLocalExists.Overwrite, token: cancellationToken);

    public async Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default) =>
        await RequireClient().DeleteFile(remotePath, cancellationToken);

    public async Task DisconnectAsync()
    {
        if (_client is not null)
        {
            await _client.Disconnect();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    private AsyncFtpClient RequireClient() =>
        _client ?? throw new InvalidOperationException("Not connected — call ConnectAsync first.");
}
