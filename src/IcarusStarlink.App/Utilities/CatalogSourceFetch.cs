using IcarusStarlink.Catalog;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Isolates one catalog source's fetch (Daedalus, Jimk72) from another run alongside it via
/// Task.WhenAll — without this, a transient failure in either source (offline, rate-limited, a
/// response shape change) would throw and discard the other source's own already-successful
/// result too, unlike how GitHubRepoDateClient/Ue4ssReleaseClient treat the same class of failure
/// as a soft, swallowed condition by design. Originally private to DownloadsViewModel's own
/// RefreshCatalogAsync; pulled out here so MergeInstallViewModel's ExportPatchAsync (which makes
/// the exact same two-source lookup to decide what needs bundling) gets the same isolation instead
/// of letting one flaky source fail the whole export.
/// </summary>
public static class CatalogSourceFetch
{
    public static async Task<IReadOnlyList<CatalogEntry>> FetchAsync(
        Func<CancellationToken, Task<IReadOnlyList<CatalogEntry>>> fetch, string sourceName, List<string> failedSources)
    {
        try
        {
            return await fetch(default);
        }
        catch (Exception)
        {
            lock (failedSources)
            {
                failedSources.Add(sourceName);
            }

            return [];
        }
    }
}
