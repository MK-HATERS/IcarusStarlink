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
}
