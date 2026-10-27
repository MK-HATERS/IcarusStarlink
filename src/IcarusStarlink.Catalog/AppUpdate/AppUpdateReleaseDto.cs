using System.Text.Json.Serialization;

namespace IcarusStarlink.Catalog.AppUpdate;

/// <summary>Only the fields this app actually reads out of GitHub's (much larger) release response.</summary>
internal sealed class AppUpdateReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    /// <summary>A beta (Beta update channel only).</summary>
    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

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
    /// GitHub release-asset digest shaped "sha256:&lt;hex&gt;". Downloads are rejected if this
    /// metadata is missing, malformed, unsupported or does not match the downloaded file.
    /// </summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}
