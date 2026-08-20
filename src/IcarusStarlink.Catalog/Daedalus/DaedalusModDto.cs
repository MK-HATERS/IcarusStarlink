using System.Text.Json.Serialization;

namespace IcarusStarlink.Catalog.Daedalus;

/// <summary>
/// Raw shape of one entry in Daedalus's mods.json, verified against the live endpoint during
/// Phase 4 planning. Deliberately no `required` members: one malformed entry among 500+
/// shouldn't be able to throw away the whole catalog fetch, so a missing field just comes
/// through as null/empty rather than failing deserialization of the entire array.
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
