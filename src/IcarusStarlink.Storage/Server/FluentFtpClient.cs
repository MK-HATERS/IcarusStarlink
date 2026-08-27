using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        if (_client is not null)
        {
            // A retry after a rejected/failed connect (e.g. the caller just saved a newly-trusted
            // certificate thumbprint and is calling this a second time) — drop the half-open
            // previous client rather than leaking its socket/TLS session.
            await _client.DisposeAsync();
        }

        _client = new AsyncFtpClient(site.Host, site.Username, password, site.Port);
        _client.Config.EncryptionMode = site.EncryptionMode switch
        {
            IcarusStarlink.Core.Server.FtpEncryptionMode.Explicit => FluentFTP.FtpEncryptionMode.Explicit,
            IcarusStarlink.Core.Server.FtpEncryptionMode.Implicit => FluentFTP.FtpEncryptionMode.Implicit,
            _ => FluentFTP.FtpEncryptionMode.None,
        };

        // Real API confirmed via reflection against the actual referenced package (FluentFTP
        // 54.2.0), not guessed: AsyncFtpClient.ValidateCertificate gives full control over whether
        // to accept a certificate FluentFTP's own default chain validation rejected. A budget
        // game-server host (the case this was built for) commonly presents a self-signed cert —
        // with no override at all, this app could never connect to hosts like that. A certificate
        // that already validates cleanly is always accepted; one that doesn't is accepted only if
        // its thumbprint matches what this site's own user previously chose to trust (mirrors
        // FileZilla/WinSCP's "trust this certificate" convention, including re-prompting rather
        // than silently trusting forever if the presented certificate ever changes). Otherwise the
        // rejection is captured here and re-thrown as a typed exception after Connect() fails, so
        // the caller can show a real prompt instead of just a generic TLS error.
        FtpUntrustedCertificateException? untrusted = null;
        _client.ValidateCertificate += (_, e) =>
        {
            if (e.PolicyErrors == SslPolicyErrors.None)
            {
                e.Accept = true;
                return;
            }

            using var cert = new X509Certificate2(e.Certificate);
            var thumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
            if (!string.IsNullOrEmpty(site.TrustedCertificateThumbprint)
                && string.Equals(thumbprint, site.TrustedCertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                e.Accept = true;
                return;
            }

            e.Accept = false;
            untrusted = new FtpUntrustedCertificateException(thumbprint, cert.Subject, cert.Issuer, e.PolicyErrorMessage);
        };

        try
        {
            await _client.Connect(cancellationToken);
        }
        catch when (untrusted is not null)
        {
            throw untrusted;
        }
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
