using System.Text;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Container;

/// <summary>Same logical layout as ExmodzArchive, but as loose files on disk rather than a zip — the "import a folder" case the Library spec calls out.</summary>
public static class ExmodFolder
{
    public static ExmodPackageContents Read(string folderPath)
    {
        string? exmodFilePath = null;
        var assetFilePaths = new List<string>();

        // Single pass over the tree: classify each file as the .EXMOD or a candidate asset,
        // instead of walking the whole directory twice (once to find the .EXMOD, again for
        // everything else).
        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            if (!filePath.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase))
            {
                assetFilePaths.Add(filePath);
                continue;
            }

            if (exmodFilePath is not null)
            {
                throw new FormatException(
                    $"Folder '{folderPath}' contains more than one .EXMOD file — ambiguous which is the mod's own package.");
            }

            exmodFilePath = filePath;
        }

        if (exmodFilePath is null)
        {
            throw new FormatException($"No .EXMOD file found under '{folderPath}'.");
        }

        // A folder can just as easily hold something accidentally (or deliberately) huge as a
        // zip can decompress to something huge — cap it the same way ExmodzArchive.Read does,
        // for the same reason, even though there's no compression/amplification step here.
        var sizeBudget = new ExmodSizeBudget($"Folder '{folderPath}'");

        var exmodBytes = ReadAllBytesBounded(exmodFilePath, $"'{exmodFilePath}'", sizeBudget);
        // TrimStart handles a leading UTF-8 BOM some tools write.
        var package = ExmodJson.Parse(Encoding.UTF8.GetString(exmodBytes).TrimStart('\uFEFF'));

        var assets = new List<ExmodAssetEntry>();
        var seenAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in assetFilePaths)
        {
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

            var content = ReadAllBytesBounded(filePath, $"Asset '{relativePath}'", sizeBudget);
            assets.Add(new ExmodAssetEntry(relativePath, content));
        }

        return new ExmodPackageContents(package, assets);
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
