using System.Text.Json.Serialization;

namespace IcarusStarlink.Catalog.AppUpdate;

/// <summary>Only the fields this app actually reads out of GitHub's (much larger) release response.</summary>
internal sealed class AppUpdateReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("assets")]
    public List<AppUpdateAssetDto> Assets { get; init; } = [];
}

internal sealed class AppUpdateAssetDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = "";

    /// <summary>
    /// GitHub's release-asset API added a content digest field shaped "sha256:&lt;hex&gt;" — this
    /// app has only been confirmed against a hand-built JSON fixture in
    /// AppUpdateClientTests, not a real captured API response, so treat the exact shape as
    /// plausible rather than certain. Left null (not defaulted to "") so
    /// AppUpdateClient.DownloadAssetAsync can tell "this response has no digest at all" apart from
    /// an empty one — either way it's treated as verification-skipped, not a failure, since an
    /// older cached response or a future shape change could omit or rename this field without that
    /// meaning the asset itself is untrustworthy. Only an actual mismatch against a present digest
    /// is treated as a real integrity failure.
    /// </summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}
