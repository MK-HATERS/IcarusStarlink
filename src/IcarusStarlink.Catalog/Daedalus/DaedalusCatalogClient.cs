using System.Net.Http.Json;
using System.Text.Json;

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
        // mods.json's own failures still throw: that is a real outage of this source. Each entry is
        // read on its own, so one of the wrong shape is skipped instead of losing all of them.
        var modsTask = httpClient.GetFromJsonAsync<List<JsonElement>>(ModsUrl, CatalogJsonOptions.Instance, cancellationToken);
        var tagsTask = FetchTagsAsync(cancellationToken);
        await Task.WhenAll(modsTask, tagsTask);

        var categoriesByModKey = BuildCategoryIndex(tagsTask.Result);

        // An explicit JSON null in a field deserializes as null despite the DTO's "" default — one
        // such entry used to NRE and take the whole catalog down, and a null description NRE'd
        // later in the search filter and crashed the app. An entry with no id,
        // name or author is unusable and skipped (the id keys the Library's Downloaded lookup, which
        // throws on a null key); other fields are coalesced.
        return [.. CatalogJsonList.ReadEach<DaedalusModDto>(modsTask.Result)
            .Where(dto => !string.IsNullOrWhiteSpace(dto.Id) && !string.IsNullOrWhiteSpace(dto.Name) && !string.IsNullOrWhiteSpace(dto.Author))
            .Select(dto => ToEntry(dto, categoriesByModKey))];
    }

    /// <summary>tags.json only adds the Category column: when it can't be had (a 404, a timeout, a broken file), every mod comes through without categories instead of the whole Daedalus catalog failing.</summary>
    private async Task<List<DaedalusTagDto>> FetchTagsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return CatalogJsonList.ReadEach<DaedalusTagDto>(
                await httpClient.GetFromJsonAsync<List<JsonElement>>(TagsUrl, CatalogJsonOptions.Instance, cancellationToken));
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private static Dictionary<(string Name, string Author), List<string>> BuildCategoryIndex(List<DaedalusTagDto> tags)
    {
        var index = new Dictionary<(string, string), List<string>>();
        foreach (var tag in tags)
        {
            if (tag is null || string.IsNullOrWhiteSpace(tag.Tag))
            {
                continue;
            }

            foreach (var modRef in tag.Mods ?? [])
            {
                // A null in the list ("mods": [null, …]) took the whole Daedalus catalog down.
                if (modRef is null || string.IsNullOrWhiteSpace(modRef.Name) || string.IsNullOrWhiteSpace(modRef.Author))
                {
                    continue;
                }

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
            dto.Version ?? "",
            dto.Description ?? "",
            dto.Compatibility ?? "",
            CompatibilityWeekParser.Parse(dto.Compatibility),
            dto.ImageUrl,
            dto.ReadmeUrl,
            WebLink.HttpsOrNull(dto.Files?.Pak),
            WebLink.HttpsOrNull(dto.Files?.Exmodz),
            categories);
    }
}
