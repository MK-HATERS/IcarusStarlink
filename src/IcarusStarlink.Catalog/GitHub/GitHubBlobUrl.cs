using System.Text.RegularExpressions;

namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// A github.com/{owner}/{repo}/blob/{ref}/{path} URL is GitHub's own HTML file-viewer page — it
/// returns real HTML (Content-Type: text/html), not the file's actual bytes. A real catalog entry
/// was confirmed live pointing an ExmodzUrl at exactly this shape (a mod author copy-pasting the
/// "view this file" URL from their browser's address bar instead of using GitHub's own "raw" link) —
/// downloading it and treating the response as an archive fails with a generic "isn't a recognized
/// archive format" error that gives no hint what actually went wrong. At the time this was found, 60
/// of 576 file URLs across 9 different authors in the live Daedalus catalog had this exact mistake,
/// confirming it's a common, easy submission error rather than a one-off.
///
/// ToRawContentUrl rewrites a blob URL to the equivalent raw.githubusercontent.com URL (confirmed
/// live: GitHub's own blob-page response even names this exact URL in its own x-raw-download
/// header), which serves the real file bytes directly. A URL that doesn't match the blob shape
/// (already a raw/releases-download link, a non-GitHub host, etc.) is returned unchanged — safe to
/// apply unconditionally to any download URL before fetching it.
/// </summary>
public static partial class GitHubBlobUrl
{
    [GeneratedRegex(@"^https://github\.com/([^/]+)/([^/]+)/blob/(.+)$")]
    private static partial Regex BlobPattern();

    public static string ToRawContentUrl(string url)
    {
        var match = BlobPattern().Match(url);
        return match.Success
            ? $"https://raw.githubusercontent.com/{match.Groups[1].Value}/{match.Groups[2].Value}/{match.Groups[3].Value}"
            : url;
    }
}
