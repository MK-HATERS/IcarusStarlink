using System.Text.Json;
using System.Text.RegularExpressions;

using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Catalog.Ue4ss;

/// <summary>
/// UE4SS-RE/RE-UE4SS on GitHub. Three places builds live: the tagged official releases (v3.0.1,
/// v4.0.0), the rolling "experimental-latest" prerelease, rebuilt from main and named like
/// "UE4SS_v4.0.0-6-gef15eba8.zip", and the "experimental2" release, which keeps older dev builds.
/// Starlink ships the official 4.0.0; this offers the newest dev build and the older ones. Every
/// call goes through GitHubApiCache and is only made when the user presses a button.
/// </summary>
public sealed partial class Ue4ssReleaseClient : IUe4ssReleaseClient
{
    private const string Repo = "https://api.github.com/repos/UE4SS-RE/RE-UE4SS";
    private const string LatestReleaseUrl = Repo + "/releases/latest";
    private const string DevBuildReleaseUrl = Repo + "/releases/tags/experimental-latest";

    /// <summary>The release tagged "experimental2": where older dev builds are kept once experimental-latest moves on.</summary>
    private const string DevBuildArchiveUrl = Repo + "/releases/tags/experimental2";
    private const string Official301ReleaseUrl = Repo + "/releases/tags/v3.0.1";

    // Matches the real "basic" (non-dev, non-Xinput-variant) asset name, e.g. "UE4SS_v3.0.1.zip" —
    // deliberately excludes the same release's own "zDEV-UE4SS_*.zip" (a much larger dev build with
    // debug symbols this app has no use for), "zCustomGameConfigs.zip", and "zMapGenBP.zip".
    [GeneratedRegex(@"^UE4SS_v[\d.]+\.zip$")]
    private static partial Regex BasicAssetName();

    [GeneratedRegex(@"^[0-9a-fA-F]{7,40}$")]
    private static partial Regex CommitShape();

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
            var body = await GitHubApiCache.GetAsync(_httpClient, LatestReleaseUrl, TimeSpan.FromHours(6), cancellationToken);
            var dto = body is null ? null : JsonSerializer.Deserialize<Ue4ssReleaseDto>(body);
            var asset = dto?.Assets.FirstOrDefault(a => BasicAssetName().IsMatch(a.Name));
            if (dto is null || asset is null)
            {
                return null;
            }

            var version = GitHubReleaseTag.StripLeadingV(dto.TagName);
            return new Ue4ssReleaseInfo(version, asset.BrowserDownloadUrl, asset.Digest);
        }
        catch (Exception)
        {
            // Offline, rate-limited, or GitHub's response shape changed — the caller falls back to
            // showing just the locally-detected version with no "latest" comparison, not a crash.
            return null;
        }
    }

    public async Task<Ue4ssReleaseInfo?> GetLatestDevBuildAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Short reuse: the build is replaced in place, and this only runs when the user asks.
            var body = await GitHubApiCache.GetAsync(_httpClient, DevBuildReleaseUrl, TimeSpan.FromMinutes(10), cancellationToken);
            var dto = body is null ? null : JsonSerializer.Deserialize<Ue4ssReleaseDto>(body);
            foreach (var asset in dto?.Assets ?? [])
            {
                if (Ue4ssBuild.TryParseDevAssetName(asset.Name, out var version, out var build, out var commit))
                {
                    return new Ue4ssReleaseInfo(version, asset.BrowserDownloadUrl, asset.Digest, build, commit);
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Ue4ssReleaseInfo>> GetOlderBuildsAsync(CancellationToken cancellationToken = default)
    {
        var builds = new List<Ue4ssReleaseInfo>();

        // The archive of dev builds: each asset name says which build it is.
        foreach (var asset in await ReleaseAssetsAsync(DevBuildArchiveUrl, cancellationToken))
        {
            if (Ue4ssBuild.TryParseDevAssetName(asset.Name, out var version, out var build, out var commit))
            {
                builds.Add(new Ue4ssReleaseInfo(version, asset.BrowserDownloadUrl, asset.Digest, build, commit));
            }
        }

        // The official 3.0.1 release: its one basic zip, known by the tag's commit.
        foreach (var asset in await ReleaseAssetsAsync(Official301ReleaseUrl, cancellationToken))
        {
            if (BasicAssetName().IsMatch(asset.Name) && Ue4ssBuild.TryParseAssetName(asset.Name, out var version, out _, out var commit))
            {
                builds.Add(new Ue4ssReleaseInfo(version, asset.BrowserDownloadUrl, asset.Digest, null, commit));
            }
        }

        // Newest first: the higher version, then the higher build number (a release itself is build 0).
        return [.. builds
            .OrderByDescending(b => Version.TryParse(b.Version, out var parsed) ? parsed : new Version(0, 0))
            .ThenByDescending(b => b.BuildNumber ?? 0)];
    }

    /// <summary>A release's assets; none when GitHub can't be reached or the answer isn't a release.</summary>
    private async Task<IReadOnlyList<Ue4ssReleaseAssetDto>> ReleaseAssetsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            // An archive and an old release rarely change, and this only runs when the user asks.
            var body = await GitHubApiCache.GetAsync(_httpClient, url, TimeSpan.FromHours(1), cancellationToken);
            return body is null ? [] : JsonSerializer.Deserialize<Ue4ssReleaseDto>(body)?.Assets ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async Task<Ue4ssBuildOrder> CompareBuildsAsync(string installedCommit, string candidateCommit, CancellationToken cancellationToken = default)
    {
        if (Ue4ssBuild.SameCommit(installedCommit, candidateCommit))
        {
            return Ue4ssBuildOrder.Same;
        }

        if (!IsCommit(installedCommit) || !IsCommit(candidateCommit))
        {
            return Ue4ssBuildOrder.Unknown;
        }

        // Dev builds are all cut from main, so a later commit date is a later build. (GitHub's
        // compare endpoint would say it directly but answers with about 1 MB per pair.)
        var installedDate = await CommitDateAsync(installedCommit, cancellationToken);
        var candidateDate = await CommitDateAsync(candidateCommit, cancellationToken);
        if (installedDate is null || candidateDate is null || installedDate == candidateDate)
        {
            return Ue4ssBuildOrder.Unknown;
        }

        return candidateDate > installedDate ? Ue4ssBuildOrder.Newer : Ue4ssBuildOrder.Older;
    }

    /// <summary>When a commit was made, from the commit list starting at it (a few KB, no file lists). A commit's date never changes, so the answer is kept for a month.</summary>
    private async Task<DateTimeOffset?> CommitDateAsync(string commit, CancellationToken cancellationToken)
    {
        try
        {
            var body = await GitHubApiCache.GetAsync(
                _httpClient, $"{Repo}/commits?sha={commit}&per_page=1", TimeSpan.FromDays(30), cancellationToken);
            if (body is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var first = document.RootElement[0];
            if (!first.TryGetProperty("sha", out var sha) || !Ue4ssBuild.SameCommit(sha.GetString(), commit))
            {
                return null;
            }

            return first.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsCommit(string? value) => value is not null && CommitShape().IsMatch(value);
}
