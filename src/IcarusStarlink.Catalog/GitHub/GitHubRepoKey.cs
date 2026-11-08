namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// Extracts (owner, repo) from the GitHub link shapes the catalogs use: github.com's
/// releases/download link for a .pak or /raw/ link for an .EXMODZ, and
/// raw.githubusercontent.com/{owner}/{repo}/{branch}/… for a file served straight from a repo.
/// All of them put owner/repo first in the path. The host must be one of those exactly:
/// "github.com/" inside another address (another host, a query string) is not a GitHub link.
/// </summary>
public static class GitHubRepoKey
{
    public static (string Owner, string Repo)? Extract(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !IsGitHubHost(uri.Host))
        {
            return null;
        }

        // Something must follow the repo (every download link has more path).
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && IsName(parts[0]) && IsName(parts[1]) ? (parts[0], parts[1]) : null;
    }

    /// <summary>github.com, www.github.com or raw.githubusercontent.com.</summary>
    internal static bool IsGitHubHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>A name GitHub allows (letters, digits, '-', '_', '.'): it becomes part of an api.github.com address.</summary>
    private static bool IsName(string part) =>
        part is not ("." or "..") && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
