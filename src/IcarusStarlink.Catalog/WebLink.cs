namespace IcarusStarlink.Catalog;

/// <summary>
/// Links that come from catalog or Nexus data (a readme link, a picture) are data, never paths or
/// commands. The readme opens through the shell and the picture is a WPF Image source, so a value
/// like "C:\Windows\System32\cmd.exe", "file:///…" or a "\\server\share\x.png" network path would
/// start a program or make Windows log in to someone else's server. Only web links pass.
/// </summary>
public static class WebLink
{
    /// <summary>The link when it is an absolute http:// or https:// web address; otherwise null.</summary>
    public static string? HttpOrNull(string? link)
    {
        var text = link?.Trim();
        if (string.IsNullOrEmpty(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.IsUnc
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return text;
    }

    /// <summary>
    /// A download link: kept only when it is https, because the file is installed into the game and a
    /// plain http download can be swapped by anyone on the network path. A plain http link to GitHub
    /// (github.com, raw.githubusercontent.com) is upgraded, since GitHub serves the same file over
    /// https; any other http link, and anything that isn't a web link, is null (no download offered).
    /// </summary>
    public static string? HttpsOrNull(string? link)
    {
        if (HttpOrNull(link) is not { } web || !Uri.TryCreate(web, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return web;
        }

        return GitHub.GitHubRepoKey.IsGitHubHost(uri.Host)
            ? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri
            : null;
    }
}
