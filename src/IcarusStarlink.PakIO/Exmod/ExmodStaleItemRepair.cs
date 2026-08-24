namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Applies a confirmed (or auto-approved) rename suggestion directly to a loaded package's own
/// in-memory rows. Deliberately does nothing else — the caller is responsible for backing up the
/// mod first, writing the package back out, and marking it locally edited.
/// </summary>
public static class ExmodStaleItemRepair
{
    public static bool RenameItem(ExmodPackage package, string currentFile, string oldName, string newName)
    {
        var row = package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, currentFile, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return false;
        }

        var renamed = false;

        // A real EXMOD can legitimately list the same item name more than once within one file
        // (see the field notes on real EXMOD mods) — every matching entry needs the same rename,
        // not just the first.
        foreach (var item in row.FileItems.Where(i => i.Name == oldName))
        {
            item.Name = newName;
            renamed = true;
        }

        return renamed;
    }
}
