namespace IcarusStarlink.Core.Server;

/// <summary>
/// Thrown by IFtpClient when the server rejects a command outright (a real protocol-level
/// completion code like "550 Permission denied"), as opposed to a network/connection failure —
/// confirmed against a real host (SurvivalServers) that allows creating new files over FTP but
/// blocks deleting or overwriting existing ones account-wide. Callers use this specifically to
/// distinguish "the server told us no" from a transient error, so a single blocked delete doesn't
/// get confused with a dropped connection. Deliberately holds only a plain string, not a
/// FluentFTP type, so that library stays an implementation detail confined to Storage's
/// FluentFtpClient.
/// </summary>
public sealed class FtpOperationRejectedException(string serverMessage)
    : Exception($"The server rejected this operation: {serverMessage}")
{
    public string ServerMessage { get; } = serverMessage;
}
