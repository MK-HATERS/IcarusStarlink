using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.App.Services;

/// <summary>One row a Library mod changes: the mod's name, the table as the mod names it ("Items-D_ItemsStatic.json") and the row.</summary>
public sealed record ModRowTouch(string ModName, string CurrentFile, string RowName);

/// <summary>
/// The rows every Library mod changes, read from each mod's EXMOD the way the health check's
/// LibraryAddedRows reads them (ExmodFolder.ReadPackageOnly). For Weekly Changes' "Only rows my
/// mods change". Prebuilt paks that were never converted have no rows to read, and merged packs
/// are left out: they hold copies of other mods' rows.
/// </summary>
public static class LibraryModRows
{
    /// <summary>Slow (reads every mod): run it off the UI thread. A mod that can't be read adds nothing.</summary>
    public static IReadOnlyList<ModRowTouch> Read(ILibraryRepository repository)
    {
        var rows = new List<ModRowTouch>();
        foreach (var entry in repository.GetAll().Where(e => !e.IsOpaquePak && e.MergedPackModNames is null && e.MergedPackOptionDescriptions is null))
        {
            try
            {
                foreach (var file in ExmodFolder.ReadPackageOnly(repository.GetFolderPath(entry.FolderName)).Rows)
                {
                    foreach (var item in file.FileItems)
                    {
                        rows.Add(new ModRowTouch(entry.Name, file.CurrentFile, item.Name));
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable mod adds nothing; the health check reports it when it checks that mod.
            }
        }

        return rows;
    }

    /// <summary>
    /// One key for a table however it is named: a mod writes "Items-D_ItemsStatic.json" (some old
    /// mods just "D_ItemsStatic.json"), a report "Items/D_ItemsStatic.json". The game's table file
    /// names are unique, so the file name alone is the key, ignoring case.
    /// </summary>
    public static string TableKey(string file)
    {
        var path = file.Replace('\\', '/').Replace('-', '/');
        return path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
    }
}
