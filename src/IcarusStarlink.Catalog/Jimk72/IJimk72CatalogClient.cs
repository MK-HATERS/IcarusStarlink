namespace IcarusStarlink.Catalog.Jimk72;

public interface IJimk72CatalogClient
{
    Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default);
}
