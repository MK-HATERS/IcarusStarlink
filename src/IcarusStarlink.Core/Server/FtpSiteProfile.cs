namespace IcarusStarlink.Core.Server;

/// <summary>
/// A saved FTP "site" — everything needed to reconnect except the password, which lives in
/// Windows Credential Manager (see CredentialTargets.FtpSite), keyed by Id rather than Name so a
/// rename doesn't orphan the saved password.
/// </summary>
public sealed class FtpSiteProfile
{
    public required Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Host { get; set; }

    public int Port { get; set; } = 21;

    public required string Username { get; set; }

    /// <summary>Remote directory to land in on connect — e.g. the server's own mods folder. Blank means whatever the server's own login directory is.</summary>
    public string RemotePath { get; set; } = "";

    public FtpEncryptionMode EncryptionMode { get; set; } = FtpEncryptionMode.None;

    /// <summary>SHA-256 thumbprint of a TLS certificate this site's user has explicitly chosen to
    /// trust despite a validation failure — not a secret, safe to store in the plain site JSON
    /// alongside everything else here. Null means "use normal certificate validation only."</summary>
    public string? TrustedCertificateThumbprint { get; set; }

    /// <summary>Whether this site's own FTP account has ever been confirmed able to delete/overwrite
    /// an existing file — some budget hosts (confirmed live against a real SurvivalServers account)
    /// allow creating new files but reject deleting or replacing one account-wide. Null means never
    /// tested; set the first time a delete either succeeds or is rejected by the server, and reused
    /// to warn before attempting a delete known to fail rather than discovering it mid-operation.</summary>
    public bool? SupportsDelete { get; set; }
}
