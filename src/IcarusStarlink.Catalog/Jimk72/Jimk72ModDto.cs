using System.Text.Json;

namespace IcarusStarlink.Catalog.Jimk72;

/// <summary>
/// Raw shape of modinfo.json — a single object wrapping a "mods" array, not a bare array like
/// Daedalus's mods.json. imageURL/readmeURL's unusual capitalization still matches PascalCase
/// ImageUrl/ReadmeUrl under System.Text.Json's case-insensitive property matching (case
/// differences only — no snake_case here, unlike Daedalus), so no JsonPropertyName needed.
/// The "mods" entries are kept raw and read one at a time (Jimk72CatalogClient), so an entry of
/// the wrong shape is skipped instead of failing the whole list.
/// </summary>
internal sealed class Jimk72ModInfoDto
{
    public List<JsonElement>? Mods { get; init; }
}

internal sealed class Jimk72ModDto
{
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Compatibility { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ImageUrl { get; init; }
    public string? ReadmeUrl { get; init; }
    public Jimk72ModFilesDto? Files { get; init; }
}

internal sealed class Jimk72ModFilesDto
{
    public string? Exmodz { get; init; }
}
