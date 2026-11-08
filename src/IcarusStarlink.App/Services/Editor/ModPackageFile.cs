using System.IO;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>The one .EXMOD of a Library mod folder, wherever in the folder it sits.</summary>
internal static class ModPackageFile
{
    /// <summary>
    /// The folder's .EXMOD, or null when the folder or the file isn't there. A folder with two
    /// can't be read (FormatException), the same rule ExmodFolder reads by.
    /// </summary>
    internal static string? Find(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return null;
        }

        string? found = null;
        // Matched by hand, not with a "*.EXMOD" pattern: a pattern follows the disk's own case
        // rules, so a second file in another case could be missed (see ExmodFolder.FindExmodFile).
        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null)
            {
                throw new FormatException($"Folder '{folderPath}' contains more than one .EXMOD file — ambiguous which is the mod's own package.");
            }

            found = file;
        }

        return found;
    }

    /// <summary>
    /// Writes the package over the folder's one .EXMOD, where it sits and under its own name
    /// (ExmodFolder.Write does that: a folder copied in from the classic mod manager keeps it at the
    /// top), or to Extracted Mods\&lt;FileName&gt;.EXMOD when the folder has none. A second .EXMOD
    /// would make the folder unreadable, so such a mod used to drop out of the Library after its
    /// first save or fix.
    /// </summary>
    internal static void Write(string folderPath, ExmodPackage package) =>
        ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
}
