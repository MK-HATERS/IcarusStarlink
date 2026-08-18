using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Container;

/// <summary>Shared validate-before-writing-anything step for both ExmodFolder.Write and ExmodzArchive.Write.</summary>
internal static class ExmodPackageWriteGuard
{
    public static void EnsureWritable(ExmodPackageContents contents)
    {
        AssetPathGuard.EnsureSimpleFileName(contents.Package.FileName);

        // Both writers place the package's own metadata at this fixed path — an asset claiming
        // the same path would get written after it and silently clobber it, undoing every
        // validation this method just did on the real package content.
        var reservedExmodPath = $"Extracted Mods/{contents.Package.FileName}.EXMOD";

        // Case-insensitive and separator-normalized: Windows filesystems (ExmodFolder's target)
        // treat paths differing only by case as the same file, and ExmodzArchive.Write normalizes
        // '\' to '/' before writing a zip entry — either kind of "different" RelativePath would
        // silently collide into one entry, clobbering the other with no error otherwise.
        var seenAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in contents.Assets)
        {
            AssetPathGuard.EnsureSafeRelativePath(asset.RelativePath);

            var normalizedPath = asset.RelativePath.Replace('\\', '/');

            if (string.Equals(normalizedPath, reservedExmodPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException(
                    $"Asset path '{asset.RelativePath}' collides with the package's own .EXMOD entry path " +
                    "— refusing to write it.");
            }

            if (!seenAssetPaths.Add(normalizedPath))
            {
                throw new FormatException(
                    $"Duplicate asset path '{asset.RelativePath}' — refusing to write a package with colliding entries.");
            }
        }

        // ToJsonObject also validates Rows/FileItems (blank/control-character CurrentFile or
        // item Name, the reserved "Name" field collision) — exercising it here, before any write
        // starts, means those failures can't corrupt a partially-written file either. The result
        // itself isn't needed; the actual write serializes again when it writes the .EXMOD entry.
        ExmodJson.ToJsonObject(contents.Package);
    }
}
