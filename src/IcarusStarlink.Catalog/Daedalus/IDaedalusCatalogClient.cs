namespace IcarusStarlink.Catalog.Daedalus;

public interface IDaedalusCatalogClient
{
    Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default);
}
