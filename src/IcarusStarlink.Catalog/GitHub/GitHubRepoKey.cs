using System.Text.RegularExpressions;

namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// Extracts (owner, repo) from any github.com URL shape the catalogs actually use — a
/// releases/download link for a .pak, or a /raw/ link for an .EXMODZ — both put owner/repo
/// directly after the host, so one pattern covers both.
/// </summary>
public static partial class GitHubRepoKey
{
    [GeneratedRegex(@"github\.com/([^/]+)/([^/]+)/")]
    private static partial Regex Pattern();

    public static (string Owner, string Repo)? Extract(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var match = Pattern().Match(url);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }
}
