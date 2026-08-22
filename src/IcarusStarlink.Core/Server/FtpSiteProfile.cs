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
}
