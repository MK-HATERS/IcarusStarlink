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

    /// <summary>Uploads a file, replacing an existing file of that name in place, without deleting it first.</summary>
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);

    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);

    Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>A plain rename (move) of a file on the server. Whether it may replace a file already at <paramref name="toRemotePath"/> is up to the server (most allow it). Throws FtpOperationRejectedException when the server refuses.</summary>
    Task RenameFileAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}
