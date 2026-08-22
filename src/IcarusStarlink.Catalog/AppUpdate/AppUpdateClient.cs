using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace IcarusStarlink.Catalog.AppUpdate;

/// <summary>
/// GitHub's own "latest release" endpoint for this app's own repo. Unlike Ue4ssReleaseClient's
/// target (a genuinely public repo), IcarusStarlink's own repo is private for now — GitHub's API
/// 404s a private repo's /releases/latest to an unauthenticated request the same way it would for a
/// repo that doesn't exist at all, and a private release's asset can't be fetched via its plain
/// browser_download_url at all (that URL only resolves inside a browser's own logged-in GitHub
/// session) — it needs the authenticated /releases/assets/{id} endpoint with an
/// Accept: application/octet-stream header instead. Both code paths here work for a public repo
/// too (a null/omitted token there just means "unauthenticated", same as any public API caller),
/// so nothing needs to change here the day the repo goes public.
/// </summary>
public sealed class AppUpdateClient(HttpClient httpClient) : IAppUpdateClient
{
    private const string UserAgent = "IcarusStarlink";
    private const string Owner = "MK-HATERS";
    private const string Repo = "IcarusStarlink";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public async Task<AppUpdateRelease?> GetLatestReleaseAsync(string? gitHubToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            ApplyHeaders(request, gitHubToken);

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

            var version = dto.TagName.StartsWith('v') ? dto.TagName[1..] : dto.TagName;
            return new AppUpdateRelease(version, dto.Body ?? "", asset.Id, asset.BrowserDownloadUrl);
        }
        catch (Exception)
        {
            // Offline, rate-limited, or GitHub's response shape changed — the caller falls back to
            // showing just the currently-installed version with no "latest" comparison, not a crash.
            return null;
        }
    }

    public async Task DownloadAssetAsync(AppUpdateRelease release, string? gitHubToken, string destinationPath, CancellationToken cancellationToken = default)
    {
        // The authenticated asset-id endpoint whenever a token is available — it's the only path
        // that works for a private repo, and works equally well for a public one. Only fall back to
        // the plain browser_download_url when there's no token to authenticate with at all.
        using var request = string.IsNullOrWhiteSpace(gitHubToken)
            ? new HttpRequestMessage(HttpMethod.Get, release.AssetBrowserDownloadUrl)
            : new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repo}/releases/assets/{release.AssetId}");
        ApplyHeaders(request, gitHubToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(destinationPath);
        await contentStream.CopyToAsync(fileStream, cancellationToken);
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? gitHubToken)
    {
        if (!request.Headers.UserAgent.Any())
        {
            request.Headers.UserAgent.ParseAdd(UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(gitHubToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("token", gitHubToken);
        }
    }
}
