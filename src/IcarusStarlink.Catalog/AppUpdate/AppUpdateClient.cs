using System.Net.Http.Json;
using System.Security.Cryptography;
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
            return new AppUpdateRelease(version, dto.Body ?? "", asset.BrowserDownloadUrl, asset.Digest);
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

        // Both streams are disposed (and the file handle released) before VerifyIntegrityAsync
        // re-opens the same path to hash it — a using-declaration here would keep fileStream open
        // for the rest of the method and turn that re-open into a sharing-violation bug.
        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var fileStream = File.Create(destinationPath))
        {
            await contentStream.CopyToAsync(fileStream, cancellationToken);
        }

        await VerifyIntegrityAsync(release.AssetDigest, destinationPath, cancellationToken);
    }

    /// <summary>
    /// GitHub's release-asset API can include a "digest" field shaped "sha256:&lt;hex&gt;" (see
    /// AppUpdateAssetDto.Digest's own doc comment for exactly what is and isn't confirmed about
    /// that shape). A null digest — field absent from this response, an older cached response, or
    /// GitHub changing the shape again — is deliberately NOT a failure: this only ever hard-fails
    /// on a digest that IS present and does not match, which means the bytes just written to disk
    /// are not what GitHub says it published and must not be handed off to UpdateApplier.Apply.
    /// </summary>
    private static async Task VerifyIntegrityAsync(string? digest, string filePath, CancellationToken cancellationToken)
    {
        const string sha256Prefix = "sha256:";
        if (digest is null || !digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expectedHex = digest[sha256Prefix.Length..];

        byte[] actualHashBytes;
        await using (var fileStream = File.OpenRead(filePath))
        {
            actualHashBytes = await SHA256.HashDataAsync(fileStream, cancellationToken);
        }

        var actualHex = Convert.ToHexString(actualHashBytes);
        if (string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch (Exception)
        {
            // Best-effort — the integrity failure below is what actually matters; a leftover temp
            // file on top of it doesn't change that this download must be rejected.
        }

        throw new InvalidOperationException("Downloaded update failed integrity verification — aborting.");
    }
}
