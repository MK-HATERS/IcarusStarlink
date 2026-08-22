namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Staging for UE4SS (Lua/scripting) mods — a separate, much simpler sibling to ILibraryRepository:
/// a UE4SS mod carries no metadata file of its own (unlike EXMOD), so its folder name on disk is
/// its only real identity. "Staged" means sitting in this app's own Staged_UE4SS folder — a mod is
/// either enabled (its folder is in the real game's own Mods folder) or disabled (staged here
/// instead); see IUe4ssModStateService for the actual enable/disable move logic (Phase 8.5).
/// UE4SS mods are NOT attached to a Profile or installed alongside the pak — that was the original
/// Phase 6.5 design, replaced entirely in Phase 8.5.
/// </summary>
public interface IUe4ssModRepository
{
    IReadOnlyList<string> GetAll();

    /// <summary>Extracts a UE4SS mod zip into staging, deriving a folder name from the zip itself and disambiguating on collision. Returns the folder name it landed under.</summary>
    string Import(string zipFilePath);

    void Delete(string folderName);

    string GetFolderPath(string folderName);

    /// <summary>Bare mod folder names currently present in the real game's own UE4SS Mods folder — see Ue4ssGamePaths.ResolveModsFolder for how a caller derives gameModsFolderPath. Empty if the folder doesn't exist yet (UE4SS not installed, or the Icarus Content path isn't set).</summary>
    IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath);

    /// <summary>
    /// Copies an already-installed mod (found via ListInstalledInGame but not yet staged through
    /// this app — the framework's own built-ins, or one installed by hand/another tool) into this
    /// app's own staging. A copy, never a move on its own — the real game folder is untouched here;
    /// IUe4ssModStateService.Apply is what completes the "disable" move by deleting the source after
    /// calling this. Returns the folder name it landed under (disambiguated on collision, same as
    /// Import).
    /// </summary>
    string AdoptFromGame(string gameModsFolderPath, string folderName);
}
