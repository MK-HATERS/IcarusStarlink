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

    /// <summary>
    /// Extracts a UE4SS mod zip into staging, deriving a folder name from the zip itself and
    /// disambiguating on collision. Returns the folder name it landed under. namesAlreadyInUse, if
    /// given, is ALSO avoided alongside this repository's own staged names — pass the real game
    /// Mods folder's own current contents (ListInstalledInGame) so a newly-imported mod can never
    /// land under the same folder name as an already-ENABLED, completely unrelated mod: without
    /// this, GetAll's own name-keyed union of staged+installed mods would silently treat the two as
    /// "the same mod" (one of them becomes invisible/inaccessible in the UI, still present on disk
    /// but with no way to select or enable it) purely because staging's own uniqueness check never
    /// looked at what the game folder already had.
    /// </summary>
    string Import(string zipFilePath, IReadOnlyCollection<string>? namesAlreadyInUse = null);

    /// <summary>
    /// Stages a UE4SS mod from an already-extracted folder — e.g. Downloads' Activate, which
    /// extracts a downloaded archive first and only then determines it's a UE4SS mod rather than
    /// an EXMOD or a prebuilt pak. Mirrors Import's own "strip a single wrapping folder if
    /// present" logic, but there's no zip filename to fall back on for naming when there isn't
    /// one — fallbackName (the original archive's own name, sans extension) covers that case.
    /// Returns the folder name it landed under (disambiguated on collision, same as Import).
    /// namesAlreadyInUse — see Import's own doc comment for why.
    /// </summary>
    string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null);

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
    /// Import). namesAlreadyInUse — see Import's own doc comment for why (here, typically every
    /// OTHER currently-installed name, so this adopted copy can't collide with one of its own
    /// siblings still left enabled).
    /// </summary>
    string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null);
}
