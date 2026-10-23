using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Copies a staged pak into the real game's Content\Paks\mods — the one thing this app ever writes
/// there. Backup scope is deliberately narrow: only the one file this service is about to
/// overwrite, not the whole folder. UE4SS mods are a separate concern (IUe4ssModStateService,
/// Phase 8.5) — this service no longer touches the UE4SS Mods folder at all. Prebuilt/opaque paks
/// are folded into the same staged pak by RebuildService itself — matching classic IMM's own real
/// behavior, so there's only ever the one file for this service to install.
/// </summary>
public sealed class InstallService : IInstallService
{
    public Task<InstallResult> InstallAsync(
        string stagedPakPath, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default, bool keepArcFissionDataPak = true) => Task.Run(() =>
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
            // deleting it only makes the library write it again (and ask for a restart). Only while
            // the library is installed: a leftover one is old data and goes like any other file.
            if (keepArcFissionDataPak && InstallManifestNames.IsArcFissionDataPak(existingFileName))
            {
                continue;
            }

            FolderBackup.BackupFile(existingFile, backupDirectory);
            File.Delete(existingFile);
        }

        var targetPakPath = Path.Combine(modsDirectory, targetPakFileName);
        var backupPakPath = FolderBackup.BackupFile(targetPakPath, backupDirectory);

        // Via a sibling temp file plus rename (AtomicFile), never File.Copy(overwrite) straight onto
        // the live pak (an audit finding): that truncates the destination first and streams in
        // place, so a copy that fails midway (disk full, antivirus lock, the game launched mid-copy)
        // left a truncated merged pak in the real game folder. The rename is all-or-nothing.
        AtomicFile.Copy(stagedPakPath, targetPakPath);

        // Same deterministic sibling path RebuildService.WriteManifest always writes to — derived
        // here rather than cached by the caller, so a manifest from an earlier Rebuild (this
        // session or a previous one) is never silently missed just because the in-memory path
        // wasn't carried forward (e.g. after an app restart).
        var stagedManifestPath = Path.Combine(Path.GetDirectoryName(stagedPakPath)!, InstallManifestNames.PakManifest);
        var targetManifestPath = Path.Combine(modsDirectory, InstallManifestNames.PakManifest);
        // Backed up too, same as the pak itself — without this, installing a pak built from a
        // smaller queue than what's currently listed silently loses the previous manifest (and
        // its "installed mods" record GetInstalledStateAsync reads below) with no way to
        // recover what was previously installed.
        FolderBackup.BackupFile(targetManifestPath, backupDirectory, BackupCleanup.MergedPakBackupsKept);
        if (File.Exists(stagedManifestPath))
        {
            AtomicFile.Copy(stagedManifestPath, targetManifestPath);
        }
        else if (File.Exists(targetManifestPath))
        {
            // No list beside the staged pak: the game's old one describes the previous build, and
            // the Library's "In game" read it as this one's (review 2026-10-23). Backed up above.
            File.Delete(targetManifestPath);
        }

        // The merged pak's backups counted across its current and earlier names, so a rename
        // doesn't start another pile of ~130 MB copies.
        BackupCleanup.PruneMergedPakBackups(backupDirectory);

        return new InstallResult(targetPakPath, backupPakPath);
    }, cancellationToken);

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
        var pakPaths = InstallManifestNames.AllMergedPakFileNames
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

    public Task<PreviousInstall?> FindPreviousInstallAsync(string backupDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() => BackupCleanup.FindNewestMergedPakBackup(backupDirectory), cancellationToken);

    public Task<PreviousInstall?> RestorePreviousInstallAsync(
        string stagedPakFileName, string icarusContentPath, string backupDirectory,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        // Chosen before anything below is backed up, or the copy of what's installed now would be
        // the newest backup and "restore" would put the same build straight back.
        if (BackupCleanup.FindNewestMergedPakBackup(backupDirectory) is not { } previous)
        {
            return null;
        }

        var modsDirectory = Path.Combine(icarusContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        var targetPakPath = Path.Combine(modsDirectory, Path.GetFileName(stagedPakFileName));
        var targetManifestPath = Path.Combine(modsDirectory, InstallManifestNames.PakManifest);

        // What's installed now is backed up first, so the restore itself can be undone.
        var pakPaths = InstallManifestNames.AllMergedPakFileNames
            .Select(name => Path.Combine(modsDirectory, name))
            .Prepend(targetPakPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var pakPath in pakPaths.Where(File.Exists))
        {
            FolderBackup.BackupFile(pakPath, backupDirectory);
            if (!pakPath.Equals(targetPakPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(pakPath);
            }
        }

        FolderBackup.BackupFile(targetManifestPath, backupDirectory, BackupCleanup.MergedPakBackupsKept);

        AtomicFile.Copy(previous.PakBackupPath, targetPakPath);
        if (previous.ManifestBackupPath is { } manifestBackup)
        {
            AtomicFile.Copy(manifestBackup, targetManifestPath);
        }
        else if (File.Exists(targetManifestPath))
        {
            // The current build's list would describe a pak that's no longer there.
            File.Delete(targetManifestPath);
        }

        // The restored pair is still the second newest (after the copy of what was installed just
        // now), so it's kept.
        BackupCleanup.PruneMergedPakBackups(backupDirectory);

        return previous;
    }, cancellationToken);
}
