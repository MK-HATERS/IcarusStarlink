using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.PakIO.Install;

public interface IUe4ssLoaderInstallService
{
    /// <summary>Reads whatever's really there right now — dwmapi.dll/UE4SS.dll existence and the version UE4SS itself wrote into UE4SS.log on its last launch. Never throws; "not installed" if nothing's found.</summary>
    Ue4ssLoaderStatus GetStatus(string icarusContentPath);

    /// <summary>
    /// Installs downloadedZipPath's UE4SS release. The archive is checked (layout and every entry)
    /// before anything is written; then the whole existing ue4ss\ folder plus dwmapi.dll are backed
    /// up (keep-last-5) and the files written: dwmapi.dll to Binaries\Win64\, last, and everything
    /// else (UE4SS.dll, docs, Mods\) to Binaries\Win64\ue4ss\. If a file can't be written, the
    /// files already written are put back from that backup. Mods\shared\ (UE4SS's Lua library) is
    /// replaced; the rest of Mods\ and the player's UE4SS-settings.ini are only added to, never
    /// overwritten. The release's own settings file is written beside the player's as
    /// UE4SS-settings.default.ini (UE4SS never reads it). Caller is responsible for the real,
    /// hard-to-reverse-write gate: this method performs the write unconditionally once called.
    /// </summary>
    Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same install, for a build Starlink knows (the bundled one, a GitHub dev build, a zip
    /// named like a release). UE4SS only rewrites UE4SS.log on the game's next start, so the stale
    /// log is dropped and the installed version/commit recorded (Ue4ssInstalledBuild), letting
    /// GetStatus name the new build straight away.
    /// </summary>
    Task InstallOrUpdateAsync(
        string icarusContentPath, string downloadedZipPath, string backupDirectory, string? knownVersion, string? knownCommit,
        CancellationToken cancellationToken = default) =>
        InstallOrUpdateAsync(icarusContentPath, downloadedZipPath, backupDirectory, cancellationToken);

    /// <summary>The names of the user's OWN mods currently in the game's Mods folder — everything that isn't one of UE4SS's own mods: the known built-ins plus the installed mods.json (Ue4ssFrameworkMods.In), shared\ included. What UninstallAsync will preserve; surfaced separately so the confirmation dialog can name them before anything happens.</summary>
    IReadOnlyList<string> ListUserAddedMods(string icarusContentPath);

    /// <summary>Mods in the game's Mods folder that carry their own program file (dlls\main.dll): C++ mods, which only work with the UE4SS build they were made for, so changing UE4SS can stop them loading.</summary>
    IReadOnlyList<string> ListNativeMods(string icarusContentPath) => [];

    /// <summary>
    /// Whether modName is one of UE4SS's own mods: the known built-ins plus the installed mods.json
    /// (Ue4ssFrameworkMods.In), shared\ included. The same classification ListUserAddedMods/UninstallAsync use, exposed per-name so
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
