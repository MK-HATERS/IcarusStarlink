using System.Net.Http.Json;
using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.AppUpdate;

/// <summary>GitHub's own "latest release" endpoint for this app's own public repo — every call here works fully unauthenticated, same as Ue4ssReleaseClient's own public-repo target.</summary>
public sealed class AppUpdateClient(HttpClient httpClient) : IAppUpdateClient
{
    private const string Owner = "MK-HATERS";
    private const string Repo = "IcarusStarlink";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public async Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            GitHubUserAgent.EnsureOn(request);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<AppUpdateReleaseDto>(cancellationToken);
            var asset = dto?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (dto is null || asset is null)
            {
                return null;
            }

            var version = GitHubReleaseTag.StripLeadingV(dto.TagName);
            return new AppUpdateRelease(version, dto.Body ?? "", asset.BrowserDownloadUrl);
        }
        catch (Exception)
        {
            // Offline, rate-limited, or GitHub's response shape changed — the caller falls back to
            // showing just the currently-installed version with no "latest" comparison, not a crash.
            return null;
        }
    }

    public async Task DownloadAssetAsync(AppUpdateRelease release, string destinationPath, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetBrowserDownloadUrl);
        GitHubUserAgent.EnsureOn(request);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(destinationPath);
        await contentStream.CopyToAsync(fileStream, cancellationToken);
    }
}
