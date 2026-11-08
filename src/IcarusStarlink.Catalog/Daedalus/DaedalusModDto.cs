using System.Text.Json.Serialization;

namespace IcarusStarlink.Catalog.Daedalus;

/// <summary>
/// Raw shape of one entry in Daedalus's mods.json, verified against the live endpoint. No
/// `required` members, and DaedalusCatalogClient reads each entry on its own: a missing field
/// comes through as null/empty, and an entry of the wrong shape (a number where text belongs, a
/// list where an object belongs) is skipped, so one malformed entry among 500+ never throws away
/// the whole catalog.
/// </summary>
internal sealed class DaedalusModDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Compatibility { get; init; } = "";
    public string Description { get; init; } = "";

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("readme_url")]
    public string? ReadmeUrl { get; init; }

    public DaedalusModFilesDto? Files { get; init; }
}

internal sealed class DaedalusModFilesDto
{
    public string? Pak { get; init; }
    public string? Exmodz { get; init; }
}
