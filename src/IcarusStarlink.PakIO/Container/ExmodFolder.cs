using System.Text;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Container;

/// <summary>
/// Same logical layout as ExmodzArchive, but as loose files on disk rather than a zip — the
/// "import a folder" case the Library spec calls out.
///
/// Read() and ListAssetPaths() each snapshot the folder's file list once (FindExmodFile and
/// EnumerateAssetPaths both then work off that same list) rather than each calling
/// Directory.EnumerateFiles independently — the folder is a live filesystem another process
/// could be touching mid-scan (an antivirus scan, an external edit), and two independent
/// enumerations could otherwise observe different states: EnumerateAssetPaths would only know to
/// exclude the exact exmodFilePath the earlier scan happened to find, so a second .EXMOD that
/// appeared in between would be silently ingested as a regular asset instead of tripping the
/// "ambiguous .EXMOD" check.
/// </summary>
public static class ExmodFolder
{
    public static ExmodPackageContents Read(string folderPath)
    {
        var allFiles = SnapshotFiles(folderPath);
        var exmodFilePath = FindExmodFile(allFiles, folderPath);
        var sizeBudget = new ExmodSizeBudget($"Folder '{folderPath}'");

        var package = ReadPackage(exmodFilePath, sizeBudget);

        var assets = new List<ExmodAssetEntry>();
        var seenAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in EnumerateAssetPaths(allFiles, folderPath, exmodFilePath, seenAssetPaths))
        {
            var content = ReadAllBytesBounded(Path.Combine(folderPath, relativePath), $"Asset '{relativePath}'", sizeBudget);
            assets.Add(new ExmodAssetEntry(relativePath, content));
        }

        return new ExmodPackageContents(package, ExmodAssetPathNormalizer.StripRedundantWrapperFolder(package.FileName, assets));
    }

    /// <summary>
    /// Parses just the .EXMOD JSON (name/author/version/description/Rows/...), skipping every
    /// asset file entirely — for scanning many mods at once (the Library list, its search index)
    /// where loading every mod's multi-MB binary assets into memory just to show a summary row
    /// would be wasteful. The .EXMOD itself is "tens of KB" per the real samples inspected during
    /// planning, cheap regardless of how large the mod's compiled assets are.
    /// </summary>
    public static ExmodPackage ReadPackageOnly(string folderPath)
    {
        var exmodFilePath = FindExmodFile(SnapshotFiles(folderPath), folderPath);
        return ReadPackage(exmodFilePath, new ExmodSizeBudget($"Folder '{folderPath}'"));
    }

    /// <summary>
    /// Same as ReadPackageOnly, but skips its own Directory.EnumerateFiles walk in favor of a file
    /// list the caller already has — for a caller (FolderLibraryRepository.RescanAll) that already
    /// walked the folder itself for its own reasons (e.g. to check for a .pak file too) and would
    /// otherwise pay for the exact same recursive walk twice per folder, once per caller. The
    /// caller is responsible for the list being a reasonably fresh snapshot of the same folder —
    /// same TOCTOU caveat SnapshotFiles' own callers already accept.
    /// </summary>
    public static ExmodPackage ReadPackageOnly(string folderPath, IReadOnlyList<string> precomputedFiles)
    {
        var exmodFilePath = FindExmodFile(precomputedFiles, folderPath);
        return ReadPackage(exmodFilePath, new ExmodSizeBudget($"Folder '{folderPath}'"));
    }

    /// <summary>Lists asset relative paths without reading any file's content — for a Files-tab-style listing before the user picks one to preview.</summary>
    public static IReadOnlyList<string> ListAssetPaths(string folderPath) =>
        ListAssetPaths(folderPath, SnapshotFiles(folderPath));

    /// <summary>Same as ListAssetPaths, but skips its own Directory.EnumerateFiles walk in favor of a file list the caller already has — same reasoning as ReadPackageOnly's own precomputed-list overload.</summary>
    public static IReadOnlyList<string> ListAssetPaths(string folderPath, IReadOnlyList<string> precomputedFiles)
    {
        var exmodFilePath = FindExmodFile(precomputedFiles, folderPath);
        return [.. EnumerateAssetPaths(precomputedFiles, folderPath, exmodFilePath, new HashSet<string>(StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>Reads one specific asset's bytes on demand — e.g. to preview a single file the user picked from a ListAssetPaths listing.</summary>
    public static byte[] ReadAssetContent(string folderPath, string relativePath)
    {
        var assetPath = AssetPathGuard.ResolveWithinDirectory(folderPath, relativePath);
        return ReadAllBytesBounded(assetPath, $"Asset '{relativePath}'", new ExmodSizeBudget($"Folder '{folderPath}'"));
    }

    public static void Write(string folderPath, ExmodPackageContents contents)
    {
        ExmodPackageWriteGuard.EnsureWritable(contents);

        Directory.CreateDirectory(folderPath);

        var exmodDir = Path.Combine(folderPath, "Extracted Mods");
        Directory.CreateDirectory(exmodDir);
        File.WriteAllText(
            Path.Combine(exmodDir, $"{contents.Package.FileName}.EXMOD"),
            ExmodJson.Serialize(contents.Package));

        foreach (var asset in contents.Assets)
        {
            // The strong guarantee: resolve-then-verify-containment, right at the point where
            // this actually touches the real filesystem.
            var assetPath = AssetPathGuard.ResolveWithinDirectory(folderPath, asset.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, asset.Content);
        }
    }

    private static List<string> SnapshotFiles(string folderPath) =>
        [.. Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)];

    private static string FindExmodFile(IReadOnlyList<string> allFiles, string folderPath)
    {
        string? exmodFilePath = null;

        // Manual scan + OrdinalIgnoreCase, not a "*.EXMOD" glob: Directory.EnumerateFiles'
        // pattern matching follows the filesystem's own case sensitivity, which is
        // case-sensitive on a case-sensitive volume or an opt-in-case-sensitive NTFS directory
        // (see the same note on EnumerateAssetPaths' duplicate check below) — a glob would then
        // silently miss a same-name-different-case second .EXMOD file instead of flagging the
        // ambiguity.
        foreach (var filePath in allFiles)
        {
            if (!filePath.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (exmodFilePath is not null)
            {
                throw new FormatException(
                    $"Folder '{folderPath}' contains more than one .EXMOD file — ambiguous which is the mod's own package.");
            }

            exmodFilePath = filePath;
        }

        return exmodFilePath ?? throw new FormatException($"No .EXMOD file found under '{folderPath}'.");
    }

    private static ExmodPackage ReadPackage(string exmodFilePath, ExmodSizeBudget sizeBudget)
    {
        var exmodBytes = ReadAllBytesBounded(exmodFilePath, $"'{exmodFilePath}'", sizeBudget);
        // TrimStart handles a leading UTF-8 BOM some tools write.
        return ExmodJson.Parse(Encoding.UTF8.GetString(exmodBytes).TrimStart('\uFEFF'));
    }

    private static IEnumerable<string> EnumerateAssetPaths(IReadOnlyList<string> allFiles, string folderPath, string exmodFilePath, HashSet<string> seenAssetPaths)
    {
        foreach (var filePath in allFiles)
        {
            if (string.Equals(filePath, exmodFilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(folderPath, filePath).Replace('\\', '/');
            // Matches ExmodzArchive.Read's guard — a reparse point/junction under folderPath
            // could otherwise produce a path that looks contained but isn't.
            AssetPathGuard.EnsureSafeRelativePath(relativePath);

            // Mirrors ExmodPackageWriteGuard's duplicate check on the write side — normally
            // unreachable on Windows' case-insensitive filesystem, but per-directory
            // case-sensitivity is an opt-in NTFS feature since Windows 10, so it's not truly
            // impossible.
            if (!seenAssetPaths.Add(relativePath))
            {
                throw new FormatException($"Duplicate asset path '{relativePath}' found while importing '{folderPath}'.");
            }

            yield return relativePath;
        }
    }

    /// <summary>
    /// Reads a whole file via a single open handle, checking its size against the budget from
    /// that same handle's Length rather than a separate File.Exists/FileInfo query beforehand —
    /// narrows (though doesn't eliminate) the window between "checked the size" and "read the
    /// content" that a plain FileInfo-then-File.ReadAllBytes sequence would leave open.
    /// </summary>
    private static byte[] ReadAllBytesBounded(string filePath, string description, ExmodSizeBudget sizeBudget)
    {
        using var stream = File.OpenRead(filePath);
        sizeBudget.Charge(description, stream.Length);

        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
