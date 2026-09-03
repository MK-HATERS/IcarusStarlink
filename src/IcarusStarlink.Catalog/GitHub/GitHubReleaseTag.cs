namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// A GitHub release's tag_name conventionally leads with "v" ("v1.2.3"), which every version
/// comparison/display in this app expects stripped off — hand-copied into both GitHub-release-
/// facing clients (AppUpdateClient, Ue4ssReleaseClient) before this, the same duplication
/// GitHubUserAgent was already extracted to stop for the User-Agent header.
/// </summary>
public static class GitHubReleaseTag
{
    public static string StripLeadingV(string tagName) => tagName.StartsWith('v') ? tagName[1..] : tagName;
}
