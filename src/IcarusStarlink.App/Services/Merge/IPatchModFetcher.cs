using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Secrets;

namespace IcarusStarlink.App.Services;

/// <summary>
/// How Import patch gets the mods a patch references that aren't in the Library yet: through
/// the Downloads page's own Nexus and IMM Database pipelines, so a downloaded mod lands and
/// activates exactly as if the user had fetched it there. A seam so Merge &amp; Install's tests
/// can stand in a fake that drops mods straight into the Library.
/// </summary>
public interface IPatchModFetcher
{
    /// <summary>A Nexus API key is set, so a Nexus mod can be fetched.</summary>
    bool CanFetchNexus { get; }

    /// <summary>Downloads and activates a Nexus mod (its one file, or the user picks one). Reports through the Downloads page; the caller checks the Library afterwards.</summary>
    Task FetchNexusAsync(int nexusModId, string displayName);

    /// <summary>Downloads and activates an IMM Database entry. False when the catalog has no such entry any more.</summary>
    Task<bool> FetchCatalogAsync(string catalogEntryId, string displayName);
}

/// <summary>The real fetcher: the shared DownloadsViewModel (resolved lazily, it's registered after Merge &amp; Install).</summary>
public sealed class DownloadsPatchFetcher(Func<DownloadsViewModel> downloads, ICredentialStore credentials) : IPatchModFetcher
{
    public bool CanFetchNexus => credentials.Read(CredentialTargets.NexusApiKey) is not null;

    public Task FetchNexusAsync(int nexusModId, string displayName) => downloads().ResolvePrimaryFileAndFetchAsync(nexusModId, displayName);

    public async Task<bool> FetchCatalogAsync(string catalogEntryId, string displayName)
    {
        var page = downloads();
        var entry = (await page.GetOrFetchCatalogAsync()).FirstOrDefault(e => string.Equals(e.Id, catalogEntryId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        var item = await page.DownloadCatalogFileAsync(entry);
        await page.ActivateAsync(item);
        return true;
    }
}
