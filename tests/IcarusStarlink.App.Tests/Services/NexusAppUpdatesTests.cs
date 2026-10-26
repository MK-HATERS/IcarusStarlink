using IcarusStarlink.App.Services;
using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Stable updates from Starlink's Nexus page (user request 2026-10-24), betas from GitHub.</summary>
public sealed class NexusAppUpdatesTests
{
    private static AppUpdateRelease Release(string version, int? nexusFileId = null) => new(version, "", nexusFileId is null ? "https://github.com/x.zip" : "", null, nexusFileId);

    [Fact]
    public void PickStable_TakesTheNewer_AndNexusOnATie()
    {
        Assert.Equal(7, NexusAppUpdates.PickStable(Release("3.0.2", 7), Release("3.0.2"))!.NexusFileId);
        Assert.Equal("3.0.3", NexusAppUpdates.PickStable(Release("3.0.2", 7), Release("3.0.3"))!.Version); // not on Nexus yet
        Assert.Equal(7, NexusAppUpdates.PickStable(Release("3.0.4", 7), Release("3.0.3"))!.NexusFileId);
        Assert.Equal("3.0.3", NexusAppUpdates.PickStable(null, Release("3.0.3"))!.Version);
        Assert.Null(NexusAppUpdates.PickStable(null, null));
    }

    [Theory]
    [InlineData("nxm://icarus/mods/330/files/1200?key=abc&expires=1700000000&user_id=1", true)]
    [InlineData("nxm://icarus/mods/331/files/1200?key=abc&expires=1700000000&user_id=1", false)]
    [InlineData("nxm://skyrim/mods/330/files/1200?key=abc&expires=1700000000&user_id=1", false)]
    [InlineData("not a link", false)]
    public void IsStarlinkLink_OnlyForStarlinksOwnPage(string url, bool expected) =>
        Assert.Equal(expected, NexusAppUpdates.IsStarlinkLink(url));

    [Fact]
    public void DownloadPageUrl_OpensTheFilesTabOnThatFile() =>
        Assert.Equal("https://www.nexusmods.com/icarus/mods/330?tab=files&file_id=1200&nmm=1", NexusAppUpdates.DownloadPageUrl(1200));

    /// <summary>
    /// Nexus not answering in time (HttpClient's timeout is a TaskCanceledException, with nothing
    /// cancelled by the caller) escaped FindLatestStableAsync, so the whole update check failed and
    /// GitHub's answer was thrown away: no update offered, no word why.
    /// </summary>
    [Fact]
    public async Task FindLatestStable_NexusTimesOut_IsNullNotAnError()
    {
        var nexus = new FakeStarlinkNexus { FilesError = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.") };
        Assert.Null(await NexusAppUpdates.FindLatestStableAsync(nexus, new FakeGitHubUpdates(), "key"));
    }

    [Fact]
    public async Task FindLatestStable_CallerCancels_StillThrows()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var nexus = new FakeStarlinkNexus { FilesError = new TaskCanceledException() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NexusAppUpdates.FindLatestStableAsync(nexus, new FakeGitHubUpdates(), "key", cancel.Token));
    }
}

/// <summary>GitHub's side of an update: nothing but what a test sets.</summary>
internal sealed class FakeGitHubUpdates : IAppUpdateClient
{
    public List<IReadOnlyList<string>> Downloads { get; } = [];

    public Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default) => Task.FromResult<AppUpdateRelease?>(null);

    public Task DownloadAssetAsync(AppUpdateRelease release, string destinationPath, CancellationToken cancellationToken = default) =>
        DownloadAsync([release.AssetBrowserDownloadUrl], release.AssetDigest, destinationPath, null, cancellationToken);

    public Task DownloadAsync(IReadOnlyList<string> urls, string? digest, string destinationPath, IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        Downloads.Add(urls);
        throw new System.Net.Http.HttpRequestException("no network in tests");
    }
}

/// <summary>Starlink's own Nexus page: its files, a free (not Premium) account, and the download links asked for.</summary>
internal sealed class FakeStarlinkNexus : IcarusStarlink.Catalog.Nexus.INexusApiClient
{
    public List<IcarusStarlink.Catalog.Nexus.NexusModFile> Files { get; } = [];

    public Exception? FilesError { get; init; }

    public List<int> DownloadLinkFileIds { get; } = [];

    public Task<IcarusStarlink.Catalog.Nexus.NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<IcarusStarlink.Catalog.Nexus.NexusUserInfo?>(new(1, "someone", IsPremium: false, IsSupporter: false, "", ""));

    public Task<IReadOnlyList<IcarusStarlink.Catalog.Nexus.NexusDownloadLink>> GetDownloadLinksAsync(
        string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default)
    {
        DownloadLinkFileIds.Add(fileId);
        return Task.FromResult<IReadOnlyList<IcarusStarlink.Catalog.Nexus.NexusDownloadLink>>([new($"https://cdn.example/{fileId}.zip", "CDN", "cdn")]);
    }

    public Task<IReadOnlyList<IcarusStarlink.Catalog.Nexus.NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
        FilesError is { } error ? Task.FromException<IReadOnlyList<IcarusStarlink.Catalog.Nexus.NexusModFile>>(error)
            : Task.FromResult<IReadOnlyList<IcarusStarlink.Catalog.Nexus.NexusModFile>>(Files);

    public Task<IcarusStarlink.Catalog.Nexus.NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<IcarusStarlink.Catalog.Nexus.NexusModInfo>> GetModListAsync(string apiKey, string gameDomain, IcarusStarlink.Catalog.Nexus.NexusModList list, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<IcarusStarlink.Catalog.Nexus.NexusModInfo>> SearchModsAsync(string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IcarusStarlink.Catalog.Nexus.NexusModPage> ListAllModsAsync(string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<IcarusStarlink.Catalog.Nexus.NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IcarusStarlink.Catalog.Nexus.NexusEndorsementStatus> SetEndorsementAsync(string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
