namespace IcarusStarlink.Core.Server;

/// <summary>
/// Thrown by IFtpClient.ConnectAsync when the server's TLS certificate fails normal validation
/// (self-signed, unknown CA, hostname mismatch — common on budget game-server hosts) and doesn't
/// match a previously-trusted thumbprint already saved for this site. Carries enough for the
/// caller to show a real "trust this certificate?" prompt — the same thing FileZilla/WinSCP do for
/// exactly this case — rather than just failing outright. Deliberately holds only plain strings,
/// not System.Security.Cryptography types, so FluentFTP stays an implementation detail confined to
/// Storage's FluentFtpClient.
/// </summary>
public sealed class FtpUntrustedCertificateException(string thumbprint, string subject, string issuer, string reason)
    : Exception($"The server's TLS certificate isn't trusted: {reason}")
{
    public string Thumbprint { get; } = thumbprint;

    public string Subject { get; } = subject;

    public string Issuer { get; } = issuer;
}
