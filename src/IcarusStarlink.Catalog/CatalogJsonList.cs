using System.Text.Json;

namespace IcarusStarlink.Catalog;

/// <summary>
/// Reads a catalog's list one entry at a time. The catalogs are other people's JSON: an entry of
/// the wrong shape (a number where text belongs, a list where an object belongs) is skipped, so it
/// can't take every other entry down with it. A null entry is skipped too.
/// </summary>
internal static class CatalogJsonList
{
    public static List<T> ReadEach<T>(IEnumerable<JsonElement>? elements) where T : class
    {
        var items = new List<T>();
        foreach (var element in elements ?? [])
        {
            try
            {
                if (element.Deserialize<T>(CatalogJsonOptions.Instance) is { } item)
                {
                    items.Add(item);
                }
            }
            catch (JsonException)
            {
                // That entry is left out.
            }
        }

        return items;
    }
}
