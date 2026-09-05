using System.IO;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Makes an untrusted, externally-sourced candidate file name (a CDN response's own
/// Content-Disposition header, or a URL's own path segment — DownloadsViewModel.FetchAndDownloadAsync)
/// safe to combine into a real file path. Strips any directory/drive-rooted prefix down to a bare
/// name (closing the path-traversal/arbitrary-write risk a raw "../../x" or "C:\x" value would
/// otherwise pose via Path.Combine), then delegates the rest (invalid characters, trailing dots/
/// spaces, reserved device names) to AssetPathGuard.SanitizeToSimpleFileName — the one shared
/// implementation FolderLibraryRepository.SanitizeFolderNameCandidate now also uses, instead of
/// each carrying its own hand-copied (and, for a while, subtly buggy) version of the same logic.
/// </summary>
public static class DownloadFileNameSanitizer
{
    public static string Sanitize(string candidate) =>
        AssetPathGuard.SanitizeToSimpleFileName(Path.GetFileName(candidate), $"download_{Guid.NewGuid():N}");

    /// <summary>
    /// A CDN's Content-Disposition file name has no uniqueness guarantee across different mods/files
    /// — two unrelated Nexus (modId, fileId) downloads can share the exact same literal name (a
    /// generic "main.zip", or the same file name reused across a mod's own version updates).
    /// Colliding at <paramref name="directory"/>/<paramref name="candidateFileName"/> would silently
    /// overwrite a DIFFERENT already-downloaded file on disk while DownloadsViewModel adds a brand
    /// new PendingDownloadEntry that claims the very same LocalFilePath — corrupting whichever entry
    /// gets Activated/Reinstalled later. A collision against THIS exact (modId, fileId) pair's own
    /// prior download is fine (a re-download legitimately replacing itself, same path as before) —
    /// <paramref name="pathAlreadyBelongsToThisDownload"/> is how the caller tells the two apart
    /// (checking its own PendingDownloadEntry records) — only a collision against something else (a
    /// different pair's download, or an untracked stray file) gets disambiguated.
    /// </summary>
    public static string ResolveUniqueFileName(
        string directory, string candidateFileName, int modId, int fileId, Func<string, bool> pathAlreadyBelongsToThisDownload)
    {
        var candidatePath = Path.Combine(directory, candidateFileName);
        if (!File.Exists(candidatePath) || pathAlreadyBelongsToThisDownload(candidatePath))
        {
            return candidateFileName;
        }

        return $"{Path.GetFileNameWithoutExtension(candidateFileName)}_{modId}-{fileId}{Path.GetExtension(candidateFileName)}";
    }
}
