using System.Net.Http.Json;
using System.Text.RegularExpressions;

using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.Ue4ss;

/// <summary>
/// GitHub's own "latest release" endpoint for UE4SS-RE/RE-UE4SS (confirmed the real repo/asset
/// naming via gh api during planning, not guessed) — GetLatestRelease excludes prereleases/drafts
/// by GitHub's own definition, so this naturally lands on the latest STABLE tag (currently v3.0.1),
/// never the rolling "experimental-latest" prerelease build, matching the user's own explicit
/// choice of "latest stable only" for Phase 8.5.
/// </summary>
public sealed partial class Ue4ssReleaseClient : IUe4ssReleaseClient
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/latest";

    // Matches the real "basic" (non-dev, non-Xinput-variant) asset name, e.g. "UE4SS_v3.0.1.zip" —
    // deliberately excludes the same release's own "zDEV-UE4SS_*.zip" (a much larger dev build with
    // debug symbols this app has no use for), "zCustomGameConfigs.zip", and "zMapGenBP.zip".
    [GeneratedRegex(@"^UE4SS_v[\d.]+\.zip$")]
    private static partial Regex BasicAssetName();

    private readonly HttpClient _httpClient;

    public Ue4ssReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        GitHubUserAgent.EnsureOn(_httpClient);
    }

    public async Task<Ue4ssReleaseInfo?> GetLatestStableReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await _httpClient.GetFromJsonAsync<Ue4ssReleaseDto>(LatestReleaseUrl, cancellationToken);
            var asset = dto?.Assets.FirstOrDefault(a => BasicAssetName().IsMatch(a.Name));
            if (dto is null || asset is null)
            {
                return null;
            }

            var version = dto.TagName.StartsWith('v') ? dto.TagName[1..] : dto.TagName;
            return new Ue4ssReleaseInfo(version, asset.BrowserDownloadUrl);
        }
        catch (Exception)
        {
            // Offline, rate-limited, or GitHub's response shape changed — the caller falls back to
            // showing just the locally-detected version with no "latest" comparison, not a crash.
            return null;
        }
    }
}
