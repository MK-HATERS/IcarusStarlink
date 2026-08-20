using System.Text.Json;

namespace IcarusStarlink.Catalog;

/// <summary>
/// Neither catalog's JSON matches C# PascalCase properties by name exactly (Daedalus is
/// lowercase/snake_case, Jimk72 is lowercase-with-occasional-caps) — GetFromJsonAsync's own
/// default JsonSerializerOptions is case-*sensitive*, so without this, "id"/"name"/"author"/etc.
/// would silently deserialize to their default values instead of throwing, since every DTO
/// property here is optional (no `required`) by design.
/// </summary>
internal static class CatalogJsonOptions
{
    public static readonly JsonSerializerOptions Instance = new() { PropertyNameCaseInsensitive = true };
}
