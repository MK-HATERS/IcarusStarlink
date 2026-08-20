namespace IcarusStarlink.Catalog.Daedalus;

/// <summary>
/// Raw shape of one entry in Daedalus's tags.json — a category bucket ("building", "mining", ...)
/// with a denormalized list of the mods carrying it. Only Name/Author are mapped from each nested
/// mod reference; the rest of that object (source, mod_page_url, file_types, ...) isn't used here.
/// </summary>
internal sealed class DaedalusTagDto
{
    public string Tag { get; init; } = "";
    public List<DaedalusTagModRefDto>? Mods { get; init; }
}

internal sealed class DaedalusTagModRefDto
{
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
}
