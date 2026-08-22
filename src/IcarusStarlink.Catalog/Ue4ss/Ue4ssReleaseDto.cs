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
}
