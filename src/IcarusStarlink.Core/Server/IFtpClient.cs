namespace IcarusStarlink.Core.Server;

/// <summary>
/// A single FTP session — connect once, then browse/upload/download/delete against it, disconnect
/// when done. One instance = one connection; get a fresh instance (see the App's own factory
/// registration) rather than reusing one across reconnects.
/// </summary>
public interface IFtpClient : IAsyncDisposable
{
    Task ConnectAsync(FtpSiteProfile site, string password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(string remotePath, CancellationToken cancellationToken = default);

    Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);

    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);

    Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}
