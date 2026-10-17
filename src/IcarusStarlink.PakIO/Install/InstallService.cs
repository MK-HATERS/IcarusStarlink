using IcarusStarlink.Core.Library;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Copies a staged pak into the real game's Content\Paks\mods — the one thing this app ever writes
/// there. Backup scope is deliberately narrow: only the one file this service is about to
/// overwrite, not the whole folder. UE4SS mods are a separate concern (IUe4ssModStateService,
/// Phase 8.5) — this service no longer touches the UE4SS Mods folder at all. Prebuilt/opaque paks
/// are folded into the same staged pak by RebuildService itself (unpacked via UnrealPak -Extract
/// into the same staging folder before -Create runs — confirmed live that extraction is additive,
/// not destructive, against a pre-populated folder) — matching classic IMM's own real behavior, so
/// there's only ever the one file for this service to install.
/// </summary>
public sealed class InstallService : IInstallService
{
    public Task<InstallResult> InstallAsync(
        string stagedPakPath, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!File.Exists(stagedPakPath))
        {
            throw new FileNotFoundException($"No staged pak at '{stagedPakPath}' — click Rebuild first.");
        }

        var modsDirectory = Path.Combine(icarusContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);

        var targetPakFileName = Path.GetFileName(stagedPakPath);

        // Content\Paks\mods is meant to hold exactly one active merged pak — RebuildService itself
        // already folds every queued mod's own prebuilt/opaque paks into this SAME staged pak
        // before Install ever runs, so anything else already sitting here (a classic IMM merged
        // pak from before this app was adopted, a stray leftover from manual testing) is stale
        // content from an entirely different build, not part of the current merge. Left alone
        // before this fix, both the old and new merged paks loaded side by side in-game — backed
        // up (not just deleted outright) before removal, matching every other real-game-folder
        // write this app makes.
        foreach (var existingFile in Directory.GetFiles(modsDirectory))
        {
            var existingFileName = Path.GetFileName(existingFile);
            if (existingFileName.Equals(targetPakFileName, StringComparison.OrdinalIgnoreCase)
                || existingFileName.Equals(InstallManifestNames.PakManifest, StringComparison.OrdinalIgnoreCase))
            {
                continue; // handled by the backup-and-overwrite below, not stale content to clear
            }

            // ArcFission Library's own data pak: its rows are in the merge, which outranks it, and
            // deleting it only makes the library write it again (and ask for a restart).
            if (InstallManifestNames.IsArcFissionDataPak(existingFileName))
            {
                continue;
            }

            FolderBackup.BackupFile(existingFile, backupDirectory);
            File.Delete(existingFile);
        }

        var targetPakPath = Path.Combine(modsDirectory, targetPakFileName);
        var backupPakPath = FolderBackup.BackupFile(targetPakPath, backupDirectory);

        // Via a sibling temp file plus rename, never File.Copy(overwrite) straight onto the live pak
        // (an audit finding): that truncates the destination first and streams in place, so a copy
        // that fails midway (disk full, antivirus lock, the game launched mid-copy) left a truncated
        // ISL-Merged_P.pak in the real game folder. The rename is all-or-nothing.
        CopyViaTemp(stagedPakPath, targetPakPath);

        // Same deterministic sibling path RebuildService.WriteManifest always writes to — derived
        // here rather than cached by the caller, so a manifest from an earlier Rebuild (this
        // session or a previous one) is never silently missed just because the in-memory path
        // wasn't carried forward (e.g. after an app restart).
        var stagedManifestPath = Path.Combine(Path.GetDirectoryName(stagedPakPath)!, InstallManifestNames.PakManifest);
        if (File.Exists(stagedManifestPath))
        {
            var targetManifestPath = Path.Combine(modsDirectory, InstallManifestNames.PakManifest);
            // Backed up too, same as the pak itself — without this, installing a pak built from a
            // smaller queue than what's currently listed silently loses the previous manifest (and
            // its "installed mods" record GetInstalledStateAsync reads below) with no way to
            // recover what was previously installed.
            FolderBackup.BackupFile(targetManifestPath, backupDirectory, BackupCleanup.MergedPakBackupsKept);
            CopyViaTemp(stagedManifestPath, targetManifestPath);
        }

        // The merged pak's backups counted across its current and earlier names, so a rename
        // doesn't start another pile of ~130 MB copies.
        BackupCleanup.PruneMergedPakBackups(backupDirectory);

        return new InstallResult(targetPakPath, backupPakPath);
    }, cancellationToken);

    private static void CopyViaTemp(string sourcePath, string destinationPath)
    {
        var tempPath = destinationPath + ".tmp";
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Scratch only — the next install overwrites it.
                }
            }
        }
    }

    public Task<InstalledState> GetInstalledStateAsync(string icarusContentPath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var pakManifestPath = Path.Combine(icarusContentPath, "Paks", "mods", InstallManifestNames.PakManifest);
        // ModListText is the same tolerant header-plus-names parser the Import IMM mod list feature
        // uses — one format, one parser, rather than a second Skip(1) copy here that could drift.
        var modNames = File.Exists(pakManifestPath)
            ? ModListText.ParseNames(File.ReadAllText(pakManifestPath))
            : [];

        return new InstalledState(modNames);
    }, cancellationToken);

    public Task<bool> RemoveInstalledPakAsync(
        string stagedPakFileName, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var modsDirectory = Path.Combine(icarusContentPath, "Paks", "mods");
        var targetPakPath = Path.Combine(modsDirectory, Path.GetFileName(stagedPakFileName));
        var targetManifestPath = Path.Combine(modsDirectory, InstallManifestNames.PakManifest);

        var removedAnything = false;

        // The merged pak under its earlier name(s) too: one left behind by an older version still
        // loads in-game, so "removed" would be untrue while it sits there.
        var pakPaths = InstallManifestNames.LegacyMergedPakFileNames
            .Select(name => Path.Combine(modsDirectory, name))
            .Prepend(targetPakPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var pakPath in pakPaths)
        {
            if (File.Exists(pakPath))
            {
                FolderBackup.BackupFile(pakPath, backupDirectory);
                File.Delete(pakPath);
                removedAnything = true;
            }
        }

        if (File.Exists(targetManifestPath))
        {
            FolderBackup.BackupFile(targetManifestPath, backupDirectory, BackupCleanup.MergedPakBackupsKept);
            File.Delete(targetManifestPath);
            removedAnything = true;
        }

        BackupCleanup.PruneMergedPakBackups(backupDirectory);

        return removedAnything;
    }, cancellationToken);
}
