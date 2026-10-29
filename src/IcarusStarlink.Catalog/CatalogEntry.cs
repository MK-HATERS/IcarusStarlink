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

    /// <summary>
    /// Null when the catalog lists none. The live Daedalus catalog writes "pak": "" for 20 mods that
    /// only have an EXMODZ, and that blank won every `PakUrl ?? ExmodzUrl` — their Last Updated
    /// date (looked up from the GitHub repo in that link) stayed empty.
    /// </summary>
    public string? PakUrl { get; init; } = string.IsNullOrWhiteSpace(PakUrl) ? null : PakUrl.Trim();

    /// <summary>Null when the catalog lists none (a blank string counts as none, as for PakUrl).</summary>
    public string? ExmodzUrl { get; init; } = string.IsNullOrWhiteSpace(ExmodzUrl) ? null : ExmodzUrl.Trim();
}
