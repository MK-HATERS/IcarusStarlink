namespace IcarusStarlink.Catalog.Ue4ss;

/// <summary>
/// Version is the GitHub release tag with its leading "v" stripped (e.g. "3.0.1"), matching the
/// format Ue4ssLogVersionParser reads back out of a real UE4SS.log. Digest is the asset's own
/// "sha256:&lt;hex&gt;" GitHub digest (or null if that field wasn't present) — the install flow
/// verifies the downloaded zip against it via GitHubAssetIntegrity before installing, the same way
/// AppUpdateRelease.AssetDigest already gates this app's own self-update.
/// </summary>
public sealed record Ue4ssReleaseInfo(string Version, string DownloadUrl, string? Digest = null);
