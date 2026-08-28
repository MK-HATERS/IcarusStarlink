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
}
