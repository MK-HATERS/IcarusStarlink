namespace IcarusStarlink.Catalog;

/// <summary>
/// One mod as listed in a community catalog — the shape both DaedalusCatalogClient and
/// Jimk72CatalogClient map their own raw JSON DTOs into, so the Downloads UI can browse/sort/
/// filter both sources in one table without knowing which one a given row came from.
/// </summary>
public sealed record CatalogEntry(
    CatalogSource Source,
    string Id,
    string Name,
    string Author,
    string Version,
    string Description,
    string CompatibilityRaw,
    int? CompatibleWeek,
    string? ImageUrl,
    string? ReadmeUrl,
    string? PakUrl,
    string? ExmodzUrl,
    IReadOnlyList<string> Categories)
{
    /// <summary>A web link or null — see WebLink (it is shown as an image, so a network path would make Windows log in to that server).</summary>
    public string? ImageUrl { get; init; } = WebLink.HttpOrNull(ImageUrl);

    /// <summary>A web link or null — see WebLink (Readme opens it through the shell, so a file path would run that program).</summary>
    public string? ReadmeUrl { get; init; } = WebLink.HttpOrNull(ReadmeUrl);
}
