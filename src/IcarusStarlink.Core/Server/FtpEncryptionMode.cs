namespace IcarusStarlink.Core.Server;

/// <summary>Mirrors FluentFTP's own FtpEncryptionMode (None/Explicit/Implicit) as a Core-level enum so callers never need a direct reference to FluentFTP itself — that stays an implementation detail of the real IFtpClient.</summary>
public enum FtpEncryptionMode
{
    None,
    Explicit,
    Implicit,
}
