using System.Net.Http.Json;
using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.AppUpdate;

/// <summary>GitHub's own "latest release" endpoint for this app's own public repo — every call here works fully unauthenticated, same as Ue4ssReleaseClient's own public-repo target.</summary>
public sealed class AppUpdateClient(HttpClient httpClient) : IAppUpdateClient
{
    private const string Owner = "MK-HATERS";
    private const string Repo = "IcarusStarlink";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>
    /// Each release (since 2.5.0) carries a small release.json (version, notes, the zip's name and
    /// SHA-256). Fetched through GitHub's plain "latest release download" link, which isn't one of
    /// the 60-an-hour API calls, so checking for updates never uses up that limit.
    /// </summary>
    private const string LatestManifestUrl = $"https://github.com/{Owner}/{Repo}/releases/latest/download/release.json";

    /// <summary>
    /// The Beta channel's pointer: a prerelease tagged "beta" whose release.json names the newest
    /// beta (same shape as a stable release's; the zip itself is on that beta's own vX.Y.Z-beta.N
    /// release). The release workflow replaces it with each beta. Not an API call either.
    /// </summary>
    private const string BetaManifestUrl = $"https://github.com/{Owner}/{Repo}/releases/download/beta/release.json";

    private const string ReleasesListUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=20";

    /// <summary>
    /// For testing an update before it ships: when the STARLINK_UPDATE_FEED environment variable
    /// names a folder holding a release.json and its zip (the release workflow's own output), every
    /// check reads that release instead of GitHub (and Nexus), and the zip is copied from there.
    /// Nothing in the app sets it; it's for the developer's test copy.
    /// </summary>
    public string? TestFeedFolder { get; init; } =
        Environment.GetEnvironmentVariable("STARLINK_UPDATE_FEED") is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;

    public string? UpdateTestFeed => TestFeedFolder;

    public async Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (TestFeedFolder is not null)
        {
            return await ReadTestFeedAsync(cancellationToken);
        }

        if (await TryManifestAsync(LatestManifestUrl, cancellationToken) is { } fromManifest)
        {
            return fromManifest;
        }

        // A release made before release.json existed: GitHub's API (limited, so cached).
        try
        {
            var body = await GitHubApiCache.GetAsync(httpClient, LatestReleaseUrl, TimeSpan.FromHours(1), cancellationToken);
            var dto = body is null ? null : System.Text.Json.JsonSerializer.Deserialize<AppUpdateReleaseDto>(body);
            var asset = dto?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (dto is null || asset is null)
            {
                return null;
            }

            var version = GitHubReleaseTag.StripLeadingV(dto.TagName);
            return new AppUpdateRelease(version, dto.Body ?? "", asset.BrowserDownloadUrl, asset.Digest);
        }
        catch (Exception)
        {
            // Offline, rate-limited, or GitHub's response shape changed — the caller falls back to
            // showing just the currently-installed version with no "latest" comparison, not a crash.
            return null;
        }
    }

    public async Task<AppUpdateRelease?> GetLatestBetaReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (TestFeedFolder is not null)
        {
            return await ReadTestFeedAsync(cancellationToken);
        }

        if (await TryManifestAsync(BetaManifestUrl, cancellationToken) is { } fromPointer)
        {
            return fromPointer;
        }

        // No pointer (or it couldn't be read): the newest prerelease with a zip in GitHub's list
        // (an API call, so cached like the stable fallback).
        try
        {
            var body = await GitHubApiCache.GetAsync(httpClient, ReleasesListUrl, TimeSpan.FromHours(1), cancellationToken);
            var releases = body is null ? null : System.Text.Json.JsonSerializer.Deserialize<List<AppUpdateReleaseDto>>(body);
            return (releases ?? [])
                .Where(r => r.Prerelease && !r.Draft)
                .Select(r => (Release: r, Version: AppReleaseVersion.TryParse(r.TagName), Asset: r.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))))
                .Where(r => r.Version is { IsPrerelease: true } && r.Asset is not null)
                .OrderByDescending(r => r.Version)
                .Select(r => new AppUpdateRelease(r.Version!.ToString(), r.Release.Body ?? "", r.Asset!.BrowserDownloadUrl, r.Asset.Digest))
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<AppUpdateRelease?> TryManifestAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
            GitHubUserAgent.EnsureOn(request);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var manifest = await response.Content.ReadFromJsonAsync<ReleaseManifest>(cancellationToken);
            if (manifest is not { Version: { Length: > 0 } version, Asset: { Length: > 0 } asset })
            {
                return null;
            }

            version = GitHubReleaseTag.StripLeadingV(version);
            var url = $"https://github.com/{Owner}/{Repo}/releases/download/v{version}/{Uri.EscapeDataString(asset)}";
            return new AppUpdateRelease(version, manifest.Notes ?? "", url, manifest.Sha256 is { Length: > 0 } hash ? $"sha256:{hash}" : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>release.json, written by the release workflow next to the zip.</summary>
    private sealed record ReleaseManifest(
        [property: System.Text.Json.Serialization.JsonPropertyName("version")] string? Version,
        [property: System.Text.Json.Serialization.JsonPropertyName("notes")] string? Notes,
        [property: System.Text.Json.Serialization.JsonPropertyName("asset")] string? Asset,
        [property: System.Text.Json.Serialization.JsonPropertyName("sha256")] string? Sha256);

    public Task<AppUpdateRelease?> GetReleaseAsync(string version, CancellationToken cancellationToken = default) =>
        TryManifestAsync($"https://github.com/{Owner}/{Repo}/releases/download/v{GitHubReleaseTag.StripLeadingV(version)}/release.json", cancellationToken);

    /// <summary>The test feed's release.json, its zip as a file link.</summary>
    private async Task<AppUpdateRelease?> ReadTestFeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(Path.Combine(TestFeedFolder!, "release.json"));
            var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync<ReleaseManifest>(stream, cancellationToken: cancellationToken);
            if (manifest is not { Version: { Length: > 0 } version, Asset: { Length: > 0 } asset })
            {
                return null;
            }

            return new AppUpdateRelease(GitHubReleaseTag.StripLeadingV(version), manifest.Notes ?? "", new Uri(Path.Combine(TestFeedFolder!, asset)).AbsoluteUri,
                manifest.Sha256 is { Length: > 0 } hash ? $"sha256:{hash}" : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public Task DownloadAssetAsync(AppUpdateRelease release, string destinationPath, CancellationToken cancellationToken = default) =>
        DownloadAsync([release.AssetBrowserDownloadUrl], release.AssetDigest, destinationPath, null, cancellationToken);

    public async Task DownloadAsync(IReadOnlyList<string> urls, string? digest, string destinationPath, IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadOneAsync(url, destinationPath, progress, cancellationToken);
                last = null;
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
            {
                // That server didn't answer or dropped: the next one (Nexus offers several).
                last = ex;
            }
        }

        if (last is not null)
        {
            throw last;
        }

        // Require valid matching SHA-256 metadata before handing executable downloads to callers.
        await GitHubAssetIntegrity.VerifySha256Async(digest, destinationPath, cancellationToken);
    }

    private async Task DownloadOneAsync(string url, string destinationPath, IProgress<AppUpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        // The test feed's zip (TestFeedFolder): read from disk, through the same progress loop.
        HttpResponseMessage? response = null;
        Stream source;
        long? total;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            // Links are network data (Nexus hands out the server list): a file on this PC only
            // ever comes from the developer's own test feed.
            if (TestFeedFolder is null)
            {
                throw new HttpRequestException($"Not a download link: {url}");
            }

            source = File.OpenRead(uri.LocalPath);
            total = source.Length;
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            GitHubUserAgent.EnsureOn(request);
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            total = response.Content.Headers.ContentLength;
            source = await response.Content.ReadAsStreamAsync(cancellationToken);
        }

        // Both streams are disposed (and the file handle released) before VerifySha256Async
        // re-opens the same path to hash it — a using-declaration here would keep fileStream open
        // for the rest of the method and turn that re-open into a sharing-violation bug.
        using var disposeResponse = response;
        try
        {
            await using var contentStream = source;
            await using var fileStream = File.Create(destinationPath);
            var buffer = new byte[81920];
            long received = 0;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                // A few times a second is enough for a progress bar.
                if (progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new AppUpdateDownloadProgress(received, total));
                }
            }

            progress?.Report(new AppUpdateDownloadProgress(received, total));
        }
        catch
        {
            // A dropped connection or a cancel leaves a truncated zip; don't leave it lying there.
            TryDelete(destinationPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
