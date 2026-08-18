namespace IcarusStarlink.PakIO.Safety;

/// <summary>
/// Guards against path traversal ("zip slip") from untrusted EXMODZ content — an EXMODZ can come
/// from an internet download (the Phase 4 Downloads tab) or a file a stranger shared, so neither
/// its asset entry names nor its "fileName" field can be trusted to stay inside the target
/// directory or to be a plain filename at all.
/// </summary>
internal static class AssetPathGuard
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// True if a single path component (a filename, or one segment of a relative path) is safe
    /// to use on Windows. Uses Path.GetInvalidFileNameChars() rather than a hand-picked character
    /// list: a narrower check (e.g. just '/' and '\') previously missed ':', which Windows treats
    /// both as a drive qualifier (making Path.Combine silently discard the base directory) and as
    /// the NTFS alternate-data-stream separator (letting "readme.md:evil.exe" smuggle hidden
    /// content onto an existing file). Also rejects reserved device names (CON, COM1, ...), which
    /// resolve to a device rather than a normal file regardless of directory.
    /// </summary>
    private static bool IsSafePathSegment(string segment)
    {
        if (segment.Length == 0 || segment == ".." || segment == "." || segment.IndexOfAny(InvalidFileNameChars) >= 0)
        {
            return false;
        }

        // Windows reserves these names by their base name regardless of extension — "NUL.txt"
        // and "con.anything" both resolve to a device, not a normal file, so an exact-match
        // check against the whole segment would miss them.
        var dotIndex = segment.IndexOf('.');
        var baseName = dotIndex >= 0 ? segment[..dotIndex] : segment;
        return !ReservedWindowsNames.Contains(baseName);
    }

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(IsSafePathSegment);
    }

    public static void EnsureSafeRelativePath(string path)
    {
        if (!IsSafeRelativePath(path))
        {
            throw new FormatException($"Asset path '{path}' is unsafe (absolute, contains '..', or an invalid path segment) and was rejected.");
        }
    }

    /// <summary>fileName is meant to be a bare identifier, not a path — reject anything that looks like one.</summary>
    public static bool IsSimpleFileName(string name) => !string.IsNullOrWhiteSpace(name) && IsSafePathSegment(name);

    public static void EnsureSimpleFileName(string name)
    {
        if (!IsSimpleFileName(name))
        {
            throw new FormatException($"EXMOD field 'fileName' must be a simple name, not a path: '{name}'.");
        }
    }

    /// <summary>
    /// Resolves relativePath against baseDirectory and verifies the result doesn't escape it —
    /// the strong guarantee, used right before touching the real filesystem.
    ///
    /// Known residual gap: this is a string-level check (Path.GetFullPath + StartsWith), not
    /// reparse-point-aware — a directory junction/symlink already sitting inside baseDirectory
    /// could make a textually-contained path resolve outside it at the OS level. Exploiting that
    /// requires an attacker to have already planted the junction inside baseDirectory beforehand
    /// (a much higher bar than anything this guard defends against elsewhere, which all work from
    /// untrusted file *content* alone), so it's accepted for now rather than adding full
    /// per-segment reparse-point resolution.
    /// </summary>
    public static string ResolveWithinDirectory(string baseDirectory, string relativePath)
    {
        EnsureSafeRelativePath(relativePath);

        var baseFullPath = Path.GetFullPath(baseDirectory);
        var baseWithSeparator = baseFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? baseFullPath
            : baseFullPath + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(Path.Combine(baseFullPath, relativePath));

        if (!candidate.StartsWith(baseWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Asset path '{relativePath}' escapes the target directory — refusing to write it.");
        }

        return candidate;
    }
}
