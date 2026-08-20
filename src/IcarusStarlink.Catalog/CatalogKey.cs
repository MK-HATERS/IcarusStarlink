namespace IcarusStarlink.Catalog;

/// <summary>
/// The (name, author) cross-reference key used wherever two catalog-shaped data sets need to be
/// matched up without a shared ID — DaedalusCatalogClient uses it to join mods.json against
/// tags.json's separate "category -> member mods" structure, and the Downloads UI uses the same
/// normalization to cross-reference a catalog entry against an imported LibraryEntry. Trimmed and
/// lowercased so "Aberiu's Supply Crates" by " Aberiu " and "aberiu's supply crates" by "aberiu"
/// match; this is a best-effort join, not an exact one — see DaedalusCatalogClient's own doc
/// comment on the roughly 10% of tags.json references that still don't resolve.
/// </summary>
public static class CatalogKey
{
    public static (string Name, string Author) Normalize(string name, string author) =>
        (name.Trim().ToLowerInvariant(), author.Trim().ToLowerInvariant());
}
