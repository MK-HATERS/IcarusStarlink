using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Real file-moving behind IUe4ssModStateService.Apply — lives in PakIO (not Storage, where
/// IUe4ssModRepository's pure staging CRUD lives) because it needs FolderBackup's keep-last-5
/// discipline before ever removing something from the real game folder, matching InstallService's
/// and Ue4ssLoaderInstallService's own backup-before-write pattern for anything under the user's
/// real Icarus install.
///
/// A mod is on when UE4SS starts it, not merely when its folder is in the game: UE4SS starts a
/// folder only through a "Name : 1" line in mods.txt or an enabled.txt inside it (Ue4ssModStart).
/// Turning on a folder already in the game writes its enabled.txt; one copied in from staging gets
/// one too unless mods.txt already starts it. Turning off still moves the folder out of the game.
/// </summary>
public sealed class Ue4ssModStateService(IUe4ssModRepository stagingRepository) : IUe4ssModStateService
{
    public IReadOnlyList<Ue4ssModState> GetAll(string gameModsFolderPath)
    {
        var installed = stagingRepository.ListInstalledInGame(gameModsFolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = stagingRepository.GetAll();
        var allNames = installed.Concat(staged).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        var list = Directory.Exists(gameModsFolderPath) ? Ue4ssModStart.ReadList(gameModsFolderPath) : Core.Server.Ue4ssModsTxt.Parse(null);
        return [.. allNames.Select(name => !installed.Contains(name)
            ? new Ue4ssModState(name, IsEnabled: false)
            // A folder that isn't a mod (UE4SS's shared\ library) is just there: never started, never "off".
            : !Ue4ssModStart.IsMod(gameModsFolderPath, name) ? new Ue4ssModState(name, IsEnabled: true, IsInGame: true)
            : new Ue4ssModState(name, Ue4ssModStart.Starts(gameModsFolderPath, name, list), IsInGame: true, Ue4ssModStart.WhyNotStarted(gameModsFolderPath, name, list)))];
    }

    public IReadOnlyList<Ue4ssModApplyFailure> Apply(string gameModsFolderPath, IReadOnlyDictionary<string, bool> desiredEnabledByName, string backupDirectory)
    {
        Directory.CreateDirectory(gameModsFolderPath);
        var installed = stagingRepository.ListInstalledInGame(gameModsFolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = Ue4ssModStart.ReadList(gameModsFolderPath);
        var ue4ssBackupDirectory = Path.Combine(backupDirectory, "UE4SS");
        var failures = new List<Ue4ssModApplyFailure>();

        foreach (var (name, desiredEnabled) in desiredEnabledByName)
        {
            var inGame = installed.Contains(name);
            var started = inGame && Ue4ssModStart.Starts(gameModsFolderPath, name, list);

            // Off means out of the game: a folder UE4SS doesn't start is still taken out when asked.
            if (desiredEnabled ? started : !inGame)
            {
                continue;
            }

            try
            {
                if (desiredEnabled && inGame)
                {
                    // In the game but not started (mods.txt says 0, or nothing starts it): its
                    // enabled.txt is all it takes; nothing is copied or moved.
                    Ue4ssModStart.SwitchOn(gameModsFolderPath, name);
                }
                else if (desiredEnabled)
                {
                    // Disabled (staging) -> Enabled (game). Refusing an empty/missing staged
                    // folder up front, rather than letting CopyDirectory "succeed" at copying
                    // zero files, is what actually matters here: without this, the very next line
                    // would delete the real staged copy — which either never had any content in
                    // the first place, or (the more dangerous case) does have real content that
                    // GetFolderPath's own resolution just failed to find — leaving neither a
                    // working staged copy nor a working enabled one, permanently and silently.
                    var sourceFolder = stagingRepository.GetFolderPath(name);
                    if (!HasAnyFile(sourceFolder))
                    {
                        throw new InvalidOperationException($"'{name}' has no files in staging to enable.");
                    }

                    var destFolder = Path.Combine(gameModsFolderPath, name);
                    try
                    {
                        FolderBackup.CopyDirectory(sourceFolder, destFolder);
                    }
                    catch
                    {
                        // A copy that fails partway (e.g. one file briefly locked by an AV scan)
                        // must not leave a half-copied folder behind in the real game Mods
                        // directory — GetAll's own "enabled" check is just "does a folder with
                        // this name exist here", so a half-copied one would silently read back as
                        // a fully, successfully enabled mod on the very next reload.
                        if (Directory.Exists(destFolder))
                        {
                            Directory.Delete(destFolder, recursive: true);
                        }

                        throw;
                    }

                    // In the game it has to start too: a folder with no enabled.txt and no "1" line in
                    // mods.txt would sit there doing nothing.
                    if (!Ue4ssModStart.Starts(gameModsFolderPath, name, list))
                    {
                        Ue4ssModStart.SwitchOn(gameModsFolderPath, name);
                    }

                    stagingRepository.Delete(name);
                }
                else
                {
                    // Enabled (game) -> Disabled (staging). Back up the real game copy first — this
                    // is a real, hard-to-reverse removal from the user's actual install, same
                    // discipline every other write into the game folder already gets.
                    var sourceFolder = Path.Combine(gameModsFolderPath, name);
                    FolderBackup.BackupFolder(sourceFolder, ue4ssBackupDirectory);
                    // No namesAlreadyInUse needed here (unlike Import/ImportFromFolder): the
                    // adopted name is DERIVED from the game folder's own listing, which the real
                    // filesystem already guarantees has no duplicates — so it can never coincide
                    // with another still-installed game mod's name the way an externally-sourced
                    // new import's own derived name genuinely can.
                    stagingRepository.AdoptFromGame(gameModsFolderPath, name);
                    Directory.Delete(sourceFolder, recursive: true);
                }
            }
            catch (Exception ex)
            {
                // One mod's failure must not silently abort every OTHER mod in this same batch —
                // record it and keep going, so a locked file on mod #2 of 5 doesn't also skip mods
                // #3-5 with no indication they were never even attempted.
                failures.Add(new Ue4ssModApplyFailure(name, ex.Message));
            }
        }

        return failures;
    }

    private static bool HasAnyFile(string folder) =>
        Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Any();
}
