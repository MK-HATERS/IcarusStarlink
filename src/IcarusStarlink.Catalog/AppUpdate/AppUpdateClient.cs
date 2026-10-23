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

    public async Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
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

    public async Task DownloadAssetAsync(AppUpdateRelease release, string destinationPath, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetBrowserDownloadUrl);
        GitHubUserAgent.EnsureOn(request);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Both streams are disposed (and the file handle released) before VerifySha256Async
        // re-opens the same path to hash it — a using-declaration here would keep fileStream open
        // for the rest of the method and turn that re-open into a sharing-violation bug.
        try
        {
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(destinationPath);
            await contentStream.CopyToAsync(fileStream, cancellationToken);
        }
        catch
        {
            // A dropped connection or a cancel leaves a truncated zip; don't leave it lying there.
            TryDelete(destinationPath);
            throw;
        }

        // See AppUpdateAssetDto.Digest's own doc comment for exactly what is and isn't confirmed
        // about GitHub's "sha256:<hex>" digest field shape. Shared with the UE4SS loader install
        // flow — see GitHubAssetIntegrity's own doc comment.
        await GitHubAssetIntegrity.VerifySha256Async(release.AssetDigest, destinationPath, cancellationToken);
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
