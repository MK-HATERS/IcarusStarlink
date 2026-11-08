using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using IcarusStarlink.Core.Server;
using CoreIFtpClient = IcarusStarlink.Core.Server.IFtpClient;

namespace IcarusStarlink.Storage.Server;

/// <summary>
/// The Core IFtpClient on FluentFTP's AsyncFtpClient: the only place in the app that references
/// FluentFTP; everything else uses the Core IFtpClient/FtpEntry types. One instance is one
/// connection (construct, Connect, use, Disconnect/Dispose).
/// </summary>
public sealed class FluentFtpClient : CoreIFtpClient
{
    private AsyncFtpClient? _client;

    public bool IsEncrypted => _client?.IsEncrypted == true;

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
            // TLS when the host answers AUTH TLS, plain FTP when it doesn't: IsEncrypted says which.
            IcarusStarlink.Core.Server.FtpEncryptionMode.Auto => FluentFTP.FtpEncryptionMode.Auto,
            _ => FluentFTP.FtpEncryptionMode.None,
        };
        // Budget game-server hosts often present a self-signed certificate, which FluentFTP's own
        // chain check rejects. A certificate that validates is accepted; otherwise only one whose
        // SHA-256 thumbprint the user chose to trust for this site, and a changed certificate is
        // asked about again (as FileZilla and WinSCP do). A refusal is kept here and thrown as
        // FtpUntrustedCertificateException once Connect fails, so the caller can ask the user.
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

    // FluentFTP reports a transfer that failed mid-stream (connection reset) or a missing source
    // as FtpStatus.Failed — a RETURN VALUE, not an exception. Dropping it meant every caller logged
    // "Uploaded"/"Installed the merged pak" over a truncated file on a live server.
    //
    // OverwriteInPlace is a plain STOR: no existence check and no DELE first. FluentFTP's Overwrite
    // sends DELE before STOR when the file exists, and a host that refuses every delete but accepts
    // a STOR over an existing file (SurvivalServers) then failed every replace of an existing name.
    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        var status = await RequireClient().UploadFile(localPath, remotePath, FtpRemoteExists.OverwriteInPlace, createRemoteDir: true, token: cancellationToken);
        if (status != FtpStatus.Success)
        {
            throw new IOException($"Upload of '{remotePath}' did not complete ({status}) — the server may hold a partial file; try again.");
        }
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        var status = await RequireClient().DownloadFile(localPath, remotePath, FtpLocalExists.Overwrite, token: cancellationToken);
        if (status != FtpStatus.Success)
        {
            throw new IOException($"Download of '{remotePath}' did not complete ({status}) — try again.");
        }
    }

    public async Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = RequireClient();

            // CWD into the file's folder, then DELE by its bare name: some chrooted daemons refuse
            // a full-path DELE (FileZilla does the same). A host that blocks delete for the whole
            // account refuses both forms.
            var lastSlash = remotePath.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                var directory = remotePath[..lastSlash];
                var fileName = remotePath[(lastSlash + 1)..];
                // The CWD is session state: every later RELATIVE path this app sends (its default
                // Icarus/Content/Paks/mods paths have no leading slash) resolves against it, so
                // leaving it changed made the very next pak upload land in a nested wrong folder
                // while the status said "Installed". Restored no matter what.
                var previousWorkingDirectory = await client.GetWorkingDirectory(cancellationToken);
                try
                {
                    await client.SetWorkingDirectory(string.IsNullOrEmpty(directory) ? "/" : directory, cancellationToken);
                    await client.DeleteFile(fileName, cancellationToken);
                }
                finally
                {
                    await client.SetWorkingDirectory(previousWorkingDirectory, cancellationToken);
                }
            }
            else
            {
                await client.DeleteFile(remotePath, cancellationToken);
            }
        }
        catch (FtpCommandException ex)
        {
            // A real server-side rejection (e.g. "550 Permission denied") — translated to a
            // Core-level type so callers can tell "the server said no" apart from a network/
            // connection failure without depending on FluentFTP.
            throw new FtpOperationRejectedException(ex.Message);
        }
    }

    // NoCheck is a plain RNFR/RNTO. With Overwrite or OverwriteInPlace FluentFTP sends DELE on an
    // existing destination first, which a delete-blocked host refuses. Whether a rename may replace
    // an existing file is then up to the server: when it refuses, FtpCommandException below.
    public async Task RenameFileAsync(string fromRemotePath, string toRemotePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await RequireClient().MoveFile(fromRemotePath, toRemotePath, FtpRemoteExists.NoCheck, cancellationToken))
            {
                throw new FtpOperationRejectedException($"The server didn't rename '{fromRemotePath}' to '{toRemotePath}'.");
            }
        }
        catch (FtpCommandException ex)
        {
            throw new FtpOperationRejectedException(ex.Message);
        }
    }

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
