using System.Text.Json.Serialization;

namespace IcarusStarlink.Catalog.Ue4ss;

/// <summary>Only the fields this app actually reads out of GitHub's (much larger) release response.</summary>
internal sealed class Ue4ssReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";

    [JsonPropertyName("assets")]
    public List<Ue4ssReleaseAssetDto> Assets { get; init; } = [];
}

internal sealed class Ue4ssReleaseAssetDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = "";

    /// <summary>
    /// Same "sha256:&lt;hex&gt;" digest field AppUpdateAssetDto.Digest reads from this app's own
    /// releases — GitHub populates it the same way for any repo's release assets, including
    /// UE4SS-RE/RE-UE4SS's. Missing or malformed digests are rejected by GitHubAssetIntegrity
    /// before the downloaded loader can be installed.
    /// </summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}
