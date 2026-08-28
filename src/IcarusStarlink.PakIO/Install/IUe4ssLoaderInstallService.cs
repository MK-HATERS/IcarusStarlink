using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.PakIO.Install;

public interface IUe4ssLoaderInstallService
{
    /// <summary>Reads whatever's really there right now — dwmapi.dll/UE4SS.dll existence and the version UE4SS itself wrote into UE4SS.log on its last launch. Never throws; "not installed" if nothing's found.</summary>
    Ue4ssLoaderStatus GetStatus(string icarusContentPath);

    /// <summary>
    /// Backs up the whole existing ue4ss\ folder plus dwmapi.dll (keep-last-5), then extracts
    /// downloadedZipPath's real UE4SS release contents into place: dwmapi.dll goes to
    /// Binaries\Win64\, everything else (UE4SS.dll, UE4SS-settings.ini, docs) goes to
    /// Binaries\Win64\ue4ss\. The zip's own bundled Mods\ folder (the framework's built-in mods) is
    /// applied additively — a file/folder that already exists at the destination is left alone, so
    /// this never overwrites a user's own customized UE4SS-settings.ini or any already-installed
    /// mod, framework or otherwise. Caller is responsible for the real, hard-to-reverse-write gate —
    /// this method performs the write unconditionally once called.
    /// </summary>
    Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, CancellationToken cancellationToken = default);

    /// <summary>The names of the user's OWN mods currently in the game's Mods folder — everything that isn't framework-owned (listed in UE4SS's own mods.json, or the shared\ infrastructure folder). What UninstallAsync will preserve; surfaced separately so the confirmation dialog can name them before anything happens.</summary>
    IReadOnlyList<string> ListUserAddedMods(string icarusContentPath);

    /// <summary>
    /// Whether modName is one of the framework's own built-in mods (or its shared\ infrastructure
    /// folder) — the same classification ListUserAddedMods/UninstallAsync use, exposed per-name so
    /// a caller with the full known-mods union (enabled in-game AND disabled/staged in this app's
    /// own folder — see IUe4ssModStateService.GetAll) can classify every one of them correctly.
    /// ListUserAddedMods alone can't do that: it only enumerates what's currently IN the game's
    /// Mods folder, so a disabled/staged mod — built-in or not — would silently read as "not
    /// user-added" (i.e. wrongly built-in) if classified by absence from that list instead.
    /// </summary>
    bool IsFrameworkOwned(string icarusContentPath, string modName);

    /// <summary>
    /// Full UE4SS uninstall: moves every user-added mod folder back to stagedModsDirectory first
    /// (nothing of the user's is ever deleted), backs up the whole ue4ss\ folder + dwmapi.dll
    /// (keep-last-5, same rotation the installer uses), then removes dwmapi.dll and the ue4ss\
    /// folder. Icarus's own files are never touched — UE4SS lives entirely in those two paths.
    /// Caller owns the confirmation gate, same as InstallOrUpdateAsync.
    /// </summary>
    Task<Ue4ssUninstallResult> UninstallAsync(string icarusContentPath, string stagedModsDirectory, string backupDirectory, CancellationToken cancellationToken = default);
}

/// <param name="PreservedUserMods">User mod folders moved to staging (their names after any collision-suffixing), so the report can say where they went.</param>
public sealed record Ue4ssUninstallResult(IReadOnlyList<string> PreservedUserMods, string BackupPath);
