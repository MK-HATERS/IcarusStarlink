namespace IcarusStarlink.Updater;

/// <summary>
/// The updater's core: copies every file from the extracted new build over the install directory.
/// Deliberately NEVER deletes anything — user data (Extracted_Mods, Profiles, Library_Meta,
/// Cache, Pending_Downloads, Backups, settings.json, custom_skin.json, Logs, …) is safe by
/// construction rather than by a preserve-list that would go stale every time the app grows a new
/// data folder. The cost is that a file the new version no longer ships lingers on disk — inert
/// for a .NET app (unreferenced assemblies are never loaded), and a fair trade against ever
/// deleting a user's mods by omission.
/// </summary>
public static class UpdateApplier
{
    private const string AppExeName = "IcarusStarlink.App.exe";
    private const int CopyAttempts = 5;
    private const int CopyRetryDelayMs = 500;

    /// <summary>
    /// Returns the number of files copied. Throws when newFilesDirectory doesn't look like an app
    /// build at all (no IcarusStarlink.App.exe) — the one guard against being pointed at the wrong
    /// folder.
    /// </summary>
    /// <remarks>
    /// Every install-directory file this update is about to overwrite is backed up (to a fresh
    /// TEMP folder, never inside installDirectory) immediately before it's overwritten — not the
    /// whole install directory, just the files this update actually touches, which is exactly the
    /// set already being enumerated from newFilesDirectory below. If anything throws partway
    /// through the copy loop (CopyWithRetry exhausting its retries on a stuck lock, most likely),
    /// every file backed up so far is restored before the exception is re-thrown, so a failed
    /// update attempt never leaves the install directory in a mixed old/new broken state. The
    /// backup is deleted once it's no longer needed — after a successful Apply, or after a failed
    /// one that rollback fully restored — so it never lingers as TEMP clutter.
    /// </remarks>
    public static int Apply(string installDirectory, string newFilesDirectory, Action<string> log)
    {
        if (!File.Exists(Path.Combine(newFilesDirectory, AppExeName)))
        {
            throw new InvalidOperationException(
                $"'{newFilesDirectory}' doesn't contain {AppExeName} — refusing to copy something that isn't an app build.");
        }

        var backupDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"UpdateBackup_{Guid.NewGuid():N}");
        var backedUpRelativePaths = new List<string>();

        try
        {
            var copied = 0;
            foreach (var sourcePath in Directory.GetFiles(newFilesDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(newFilesDirectory, sourcePath);
                var destinationPath = Path.Combine(installDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                // Nothing to preserve for a file the new build adds that the install doesn't have
                // yet — rollback never deletes it either (see the class doc comment: this never
                // deletes anything), it's just left in place, same as any other file the new build
                // ships that an older version didn't.
                if (File.Exists(destinationPath))
                {
                    var backupPath = Path.Combine(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    CopyWithRetry(destinationPath, backupPath, log);
                    backedUpRelativePaths.Add(relativePath);
                }

                CopyWithRetry(sourcePath, destinationPath, log);
                copied++;
            }

            log($"Copied {copied} file(s) from '{newFilesDirectory}' into '{installDirectory}'.");
            TryDeleteBackupDirectory(backupDirectory, log);
            return copied;
        }
        catch (Exception ex)
        {
            log($"Update failed partway through ({ex.Message}) — rolling back {backedUpRelativePaths.Count} file(s) already overwritten.");
            if (RestoreBackup(installDirectory, backupDirectory, backedUpRelativePaths, log))
            {
                TryDeleteBackupDirectory(backupDirectory, log);
            }
            else
            {
                log($"Backup kept at '{backupDirectory}' for manual recovery — rollback couldn't restore every file.");
            }

            throw;
        }
    }

    /// <summary>
    /// Best-effort restore of every file this Apply attempt had already overwritten before it
    /// failed. Each file is tried independently — one restore failing must never stop the rest
    /// from being attempted, and must never bubble up to replace or mask the original exception
    /// that triggered this rollback (the caller re-throws that after this returns). Returns whether
    /// every backed-up file was restored.
    /// </summary>
    private static bool RestoreBackup(string installDirectory, string backupDirectory, List<string> backedUpRelativePaths, Action<string> log)
    {
        var failures = new List<string>();
        foreach (var relativePath in backedUpRelativePaths)
        {
            try
            {
                var backupPath = Path.Combine(backupDirectory, relativePath);
                var destinationPath = Path.Combine(installDirectory, relativePath);
                CopyWithRetry(backupPath, destinationPath, log);
            }
            catch (Exception ex)
            {
                failures.Add($"{relativePath} ({ex.Message})");
            }
        }

        if (failures.Count > 0)
        {
            log($"WARNING: rollback could not restore {failures.Count} file(s) — the install directory may be left in a mixed old/new state for: {string.Join(", ", failures)}");
            return false;
        }

        if (backedUpRelativePaths.Count > 0)
        {
            log("Rollback restored the install directory to its pre-update state.");
        }

        return true;
    }

    /// <summary>Best-effort cleanup only — a stray backup folder left in TEMP is a nuisance, never worth surfacing as the update's own result.</summary>
    private static void TryDeleteBackupDirectory(string backupDirectory, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(backupDirectory))
            {
                return;
            }

            // A backed-up copy inherits the ReadOnly attribute from whatever install-directory
            // file it was copied from — and a read-only destination file is a real, documented
            // scenario for this app (see CopyWithRetry's own doc comment) — which would otherwise
            // make Directory.Delete throw here and silently leave this folder behind in TEMP even
            // though the update itself succeeded.
            foreach (var backedUpFile in Directory.GetFiles(backupDirectory, "*", SearchOption.AllDirectories))
            {
                TryClearReadOnly(backedUpFile);
            }

            Directory.Delete(backupDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            log($"Couldn't clean up the update backup folder '{backupDirectory}': {ex.Message}");
        }
    }

    /// <summary>
    /// The just-exited main process can hold file handles for a moment after Process.WaitForExit
    /// returns (antivirus scans stretch this further) — a short retry loop covers that instead of
    /// failing the whole update on the first sharing violation. Also catches
    /// UnauthorizedAccessException, not just IOException: a destination file left read-only (common
    /// after some git checkouts, or AV/backup tooling flipping the attribute) throws that instead,
    /// which used to propagate uncaught and abort the update on the very first such file rather than
    /// retrying — clearing the attribute before retrying actually fixes that case, not just retries
    /// the same failure five times.
    /// </summary>
    private static void CopyWithRetry(string sourcePath, string destinationPath, Action<string> log)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < CopyAttempts)
            {
                log($"Attempt {attempt} to copy '{destinationPath}' failed ({ex.Message}) — retrying.");
                TryClearReadOnly(destinationPath);
                Thread.Sleep(CopyRetryDelayMs);
            }
        }
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — if this fails too, the retry loop's own next File.Copy attempt will
            // surface the real error (or a subsequent retry might still succeed on its own).
        }
    }
}
