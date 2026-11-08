namespace IcarusStarlink.Core.Server;

/// <summary>
/// Mirrors FluentFTP's own FtpEncryptionMode as a Core-level enum so callers never need a direct
/// reference to FluentFTP itself — that stays an implementation detail of the real IFtpClient.
/// Saved sites store it as a number: never reorder it, and add new values at the end.
/// </summary>
public enum FtpEncryptionMode
{
    None,
    Explicit,
    Implicit,

    /// <summary>TLS when the host offers it, plain FTP when it doesn't. New sites start with this one.</summary>
    Auto,
}
