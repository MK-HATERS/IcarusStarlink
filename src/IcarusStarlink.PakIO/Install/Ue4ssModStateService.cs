using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Real file-moving behind IUe4ssModStateService.Apply — lives in PakIO (not Storage, where
/// IUe4ssModRepository's pure staging CRUD lives) because it needs FolderBackup's keep-last-5
/// discipline before ever removing something from the real game folder, matching InstallService's
/// and Ue4ssLoaderInstallService's own backup-before-write pattern for anything under the user's
/// real Icarus install.
/// </summary>
public sealed class Ue4ssModStateService(IUe4ssModRepository stagingRepository) : IUe4ssModStateService
{
    public IReadOnlyList<Ue4ssModState> GetAll(string gameModsFolderPath)
    {
        var installed = stagingRepository.ListInstalledInGame(gameModsFolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = stagingRepository.GetAll();
        var allNames = installed.Concat(staged).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        return [.. allNames.Select(name => new Ue4ssModState(name, installed.Contains(name)))];
    }

    public void Apply(string gameModsFolderPath, IReadOnlyDictionary<string, bool> desiredEnabledByName, string backupDirectory)
    {
        Directory.CreateDirectory(gameModsFolderPath);
        var installed = stagingRepository.ListInstalledInGame(gameModsFolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ue4ssBackupDirectory = Path.Combine(backupDirectory, "UE4SS");

        foreach (var (name, desiredEnabled) in desiredEnabledByName)
        {
            var currentlyEnabled = installed.Contains(name);
            if (desiredEnabled == currentlyEnabled)
            {
                continue;
            }

            if (desiredEnabled)
            {
                // Disabled (staging) -> Enabled (game).
                var sourceFolder = stagingRepository.GetFolderPath(name);
                FolderBackup.CopyDirectory(sourceFolder, Path.Combine(gameModsFolderPath, name));
                stagingRepository.Delete(name);
            }
            else
            {
                // Enabled (game) -> Disabled (staging). Back up the real game copy first — this is
                // a real, hard-to-reverse removal from the user's actual install, same discipline
                // every other write into the game folder already gets.
                var sourceFolder = Path.Combine(gameModsFolderPath, name);
                FolderBackup.BackupFolder(sourceFolder, ue4ssBackupDirectory);
                stagingRepository.AdoptFromGame(gameModsFolderPath, name);
                Directory.Delete(sourceFolder, recursive: true);
            }
        }
    }
}
