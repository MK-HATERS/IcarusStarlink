using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Stable updates from Starlink's own Nexus page (user request 2026-10-24; betas stay on GitHub).
/// The newest MAIN file there is the release; its notes and SHA-256 come from the same version's
/// release.json on GitHub, which carries the very same zip. Nexus only hands a download link
/// straight to Premium accounts; anyone else gets one by pressing Mod Manager Download on the page,
/// and that nxm:// link comes back here (IsStarlinkLink) instead of being imported as a mod.
/// </summary>
public static class NexusAppUpdates
{
    /// <summary>Icarus Starlink's page: nexusmods.com/icarus/mods/330.</summary>
    public const int StarlinkModId = 330;

    /// <summary>The page's Files tab with this file picked; nmm=1 opens its Mod Manager Download.</summary>
    public static string DownloadPageUrl(int fileId) =>
        $"https://www.nexusmods.com/{NxmHandlerCommand.IcarusDomain}/mods/{StarlinkModId}?tab=files&file_id={fileId}&nmm=1";

    /// <summary>An nxm:// link for Starlink itself (an update), not a mod to import.</summary>
    public static bool IsStarlinkLink(string nxmUrl)
    {
        try
        {
            var url = NxmUrl.Parse(nxmUrl);
            return url.ModId == StarlinkModId && string.Equals(url.GameDomain, NxmHandlerCommand.IcarusDomain, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The newest stable release on the Nexus page, with GitHub's notes and hash for the same version
    /// when it has them; null when Nexus can't be asked (no key, offline) or lists no stable file.
    /// </summary>
    public static async Task<AppUpdateRelease?> FindLatestStableAsync(INexusApiClient nexus, IAppUpdateClient gitHub, string apiKey, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NexusModFile> files;
        try
        {
            files = await nexus.GetModFilesAsync(apiKey, NxmHandlerCommand.IcarusDomain, StarlinkModId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        var newest = files
            .Where(f => string.Equals(f.CategoryName, "MAIN", StringComparison.OrdinalIgnoreCase))
            .Select(f => (File: f, Version: AppReleaseVersion.TryParse(f.Version)))
            .Where(f => f.Version is { IsPrerelease: false })
            .OrderByDescending(f => f.Version)
            .FirstOrDefault();
        if (newest.File is null)
        {
            return null;
        }

        var version = newest.Version!.ToString();
        var onGitHub = await gitHub.GetReleaseAsync(version, cancellationToken);
        return new AppUpdateRelease(
            version,
            onGitHub?.ReleaseNotes is { Length: > 0 } notes ? notes : newest.File.Notes ?? "",
            onGitHub?.AssetBrowserDownloadUrl ?? "",
            onGitHub?.AssetDigest,
            newest.File.FileId);
    }

    /// <summary>The CDN links for a file: straight for Premium, or with the key and expiry from an nxm:// link.</summary>
    public static async Task<IReadOnlyList<string>> GetDownloadUrlsAsync(INexusApiClient nexus, string apiKey, int fileId, string? key, long? expires,
        CancellationToken cancellationToken = default) =>
        [.. (await nexus.GetDownloadLinksAsync(apiKey, NxmHandlerCommand.IcarusDomain, StarlinkModId, fileId, key, expires, cancellationToken)).Select(l => l.Uri)];

    /// <summary>The release to offer on the Stable channel: the newer of Nexus's and GitHub's, Nexus when they're the same version.</summary>
    public static AppUpdateRelease? PickStable(AppUpdateRelease? fromNexus, AppUpdateRelease? fromGitHub)
    {
        if (fromNexus is null || fromGitHub is null)
        {
            return fromNexus ?? fromGitHub;
        }

        var nexusVersion = AppReleaseVersion.TryParse(fromNexus.Version);
        var gitHubVersion = AppReleaseVersion.TryParse(fromGitHub.Version);
        return nexusVersion is not null && gitHubVersion is not null && gitHubVersion.CompareTo(nexusVersion) > 0 ? fromGitHub : fromNexus;
    }
}
