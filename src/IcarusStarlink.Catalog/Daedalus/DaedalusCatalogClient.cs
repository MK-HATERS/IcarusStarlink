using System.Net.Http.Json;

namespace IcarusStarlink.Catalog.Daedalus;

/// <summary>
/// Project Daedalus's live, hourly-synced static mirror — plain unauthenticated JSON, no server
/// of our own required. mods.json is the authoritative per-mod list; tags.json is a separate,
/// denormalized "category -> member mods" structure with no shared ID between the two files, so
/// categories are cross-referenced by (name, author) instead. That match isn't perfect — verified
/// against a live snapshot, about 10% of tags.json's mod references don't resolve to a current
/// mods.json entry (the two files aren't kept in perfect lockstep) — an unresolved mod just ends
/// up with no categories rather than failing the fetch.
/// </summary>
public sealed class DaedalusCatalogClient(HttpClient httpClient) : IDaedalusCatalogClient
{
    private const string ModsUrl = "https://agentkush.github.io/daedalus-static-poc/api/v1/mods.json";
    private const string TagsUrl = "https://agentkush.github.io/daedalus-static-poc/api/v1/tags.json";

    public async Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var modsTask = httpClient.GetFromJsonAsync<List<DaedalusModDto>>(ModsUrl, CatalogJsonOptions.Instance, cancellationToken);
        var tagsTask = httpClient.GetFromJsonAsync<List<DaedalusTagDto>>(TagsUrl, CatalogJsonOptions.Instance, cancellationToken);
        await Task.WhenAll(modsTask, tagsTask);

        var categoriesByModKey = BuildCategoryIndex(tagsTask.Result ?? []);

        return [.. (modsTask.Result ?? []).Select(dto => ToEntry(dto, categoriesByModKey))];
    }

    private static Dictionary<(string Name, string Author), List<string>> BuildCategoryIndex(List<DaedalusTagDto> tags)
    {
        var index = new Dictionary<(string, string), List<string>>();
        foreach (var tag in tags)
        {
            foreach (var modRef in tag.Mods ?? [])
            {
                var key = CatalogKey.Normalize(modRef.Name, modRef.Author);
                if (!index.TryGetValue(key, out var categories))
                {
                    categories = [];
                    index[key] = categories;
                }

                categories.Add(tag.Tag);
            }
        }

        return index;
    }

    private static CatalogEntry ToEntry(DaedalusModDto dto, Dictionary<(string, string), List<string>> categoriesByModKey)
    {
        var categories = categoriesByModKey.TryGetValue(CatalogKey.Normalize(dto.Name, dto.Author), out var found)
            ? (IReadOnlyList<string>)found
            : [];

        return new CatalogEntry(
            CatalogSource.Daedalus,
            dto.Id,
            dto.Name,
            dto.Author,
            dto.Version,
            dto.Description,
            dto.Compatibility,
            CompatibilityWeekParser.Parse(dto.Compatibility),
            dto.ImageUrl,
            dto.ReadmeUrl,
            dto.Files?.Pak,
            dto.Files?.Exmodz,
            categories);
    }
}
