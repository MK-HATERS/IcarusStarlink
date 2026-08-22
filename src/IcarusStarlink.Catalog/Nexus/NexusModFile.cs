namespace IcarusStarlink.Catalog.Nexus;

/// <summary>One downloadable file on a mod's Files tab — the file ID is what the download_link endpoint needs alongside the mod ID. IsPrimary marks the file Nexus itself considers the mod's main download; CategoryName is "MAIN"/"OPTIONAL"/"OLD_VERSION"/etc. per Nexus's own IFileInfo shape.</summary>
public sealed record NexusModFile(int FileId, string FileName, string Name, string Version, string? CategoryName, bool IsPrimary);
