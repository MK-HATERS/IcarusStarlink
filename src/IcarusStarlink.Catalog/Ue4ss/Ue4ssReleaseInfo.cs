namespace IcarusStarlink.Catalog.Ue4ss;

/// <summary>Version is the GitHub release tag with its leading "v" stripped (e.g. "3.0.1"), matching the format Ue4ssLogVersionParser reads back out of a real UE4SS.log.</summary>
public sealed record Ue4ssReleaseInfo(string Version, string DownloadUrl);
