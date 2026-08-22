namespace IcarusStarlink.Core.Server;

/// <summary>One file or folder in an FTP directory listing — a clean DTO independent of FluentFTP's own FtpListItem, so Core/App never need a direct reference to FluentFTP.</summary>
public sealed record FtpEntry(string Name, bool IsDirectory, long? SizeBytes, DateTimeOffset? ModifiedAt);
