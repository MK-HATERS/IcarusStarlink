using System.IO;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Makes an untrusted, externally-sourced candidate file name (a CDN response's own
/// Content-Disposition header, or a URL's own path segment — DownloadsViewModel.FetchAndDownloadAsync)
/// safe to combine into a real file path. Strips any directory/drive-rooted prefix down to a bare
/// name (closing the path-traversal/arbitrary-write risk a raw "../../x" or "C:\x" value would
/// otherwise pose via Path.Combine), replaces Windows-invalid characters (including ':', which
/// could otherwise target an NTFS alternate data stream), trims trailing dots/spaces (Windows
/// silently strips these, which would otherwise land at a different path than the sanitized name
/// implies), and dodges reserved device names — the same convention
/// FolderLibraryRepository.SanitizeFolderNameCandidate already uses for a different
/// externally-sourced name.
/// </summary>
public static class DownloadFileNameSanitizer
{
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string candidate)
    {
        var bareName = Path.GetFileName(candidate);

        var sanitized = new string([.. bareName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)])
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = $"download_{Guid.NewGuid():N}";
        }

        var dotIndex = sanitized.IndexOf('.');
        var baseName = dotIndex >= 0 ? sanitized[..dotIndex] : sanitized;
        if (ReservedWindowsDeviceNames.Contains(baseName))
        {
            sanitized = "_" + sanitized;
        }

        return sanitized;
    }
}
