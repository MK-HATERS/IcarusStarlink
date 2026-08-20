using System.Net.Http.Json;

namespace IcarusStarlink.Catalog.Jimk72;

/// <summary>
/// Jimk72's own smaller catalog, published as plain JSON on GitHub — no ID field of its own
/// (unlike Daedalus), so CatalogEntry.Id is synthesized from name+author. Verified against the
/// live endpoint during Phase 4 planning: always a single "exmodz" file, "All" compatibility,
/// no category taxonomy at all — a much simpler shape than Daedalus's.
/// </summary>
public sealed class Jimk72CatalogClient(HttpClient httpClient) : IJimk72CatalogClient
{
    private const string ModInfoUrl = "https://raw.githubusercontent.com/Jimk72/Icarus_Mods/main/modinfo.json";

    public async Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<Jimk72ModInfoDto>(ModInfoUrl, CatalogJsonOptions.Instance, cancellationToken);
        return [.. (response?.Mods ?? []).Select(ToEntry)];
    }

    private static CatalogEntry ToEntry(Jimk72ModDto dto) => new(
        CatalogSource.Jimk72,
        $"jimk72:{dto.Name}:{dto.Author}",
        dto.Name,
        dto.Author,
        dto.Version,
        dto.Description,
        dto.Compatibility,
        CompatibilityWeekParser.Parse(dto.Compatibility),
        dto.ImageUrl,
        dto.ReadmeUrl,
        null,
        dto.Files?.Exmodz,
        []);
}
