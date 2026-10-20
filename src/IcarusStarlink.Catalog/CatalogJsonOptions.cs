using System.Text.Json;

namespace IcarusStarlink.Catalog;

/// <summary>
/// Neither catalog's JSON matches C# PascalCase properties by name exactly (Daedalus is
/// lowercase/snake_case, Jimk72 is lowercase-with-occasional-caps). GetFromJsonAsync's own
/// defaults (JsonSerializerDefaults.Web) happen to be case-insensitive too, but that is an
/// implicit default; passing these options states the requirement outright, because a
/// case-sensitive read would silently leave "id"/"name"/"author"/etc. at their default values
/// instead of throwing, since every DTO property here is optional (no `required`) by design.
/// </summary>
internal static class CatalogJsonOptions
{
    public static readonly JsonSerializerOptions Instance = new() { PropertyNameCaseInsensitive = true };
}
