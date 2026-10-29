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
}
