namespace IcarusStarlink.Catalog.Ue4ss;

/// <summary>
/// Version is the GitHub release tag with its leading "v" stripped (e.g. "3.0.1"), matching the
/// format Ue4ssLogVersionParser reads back out of a real UE4SS.log. Digest is the asset's own
/// "sha256:&lt;hex&gt;" GitHub digest (or null if that field wasn't present) — the install flow
/// verifies the downloaded zip against it via GitHubAssetIntegrity before installing, the same way
/// AppUpdateRelease.AssetDigest already gates this app's own self-update.
/// </summary>
/// <param name="BuildNumber">For a dev build: how many commits after the v3.0.1 tag it is (1181 in "UE4SS_v3.0.1-1181-g83330635.zip"). Null for a tagged release.</param>
/// <param name="Commit">For a dev build: its short git commit ("83330635"). Null for a tagged release.</param>
public sealed record Ue4ssReleaseInfo(string Version, string DownloadUrl, string? Digest = null, int? BuildNumber = null, string? Commit = null);
