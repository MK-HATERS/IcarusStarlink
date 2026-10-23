namespace IcarusStarlink.Updater;

/// <summary>
/// The updater's core: copies every file from the extracted new build over the install directory.
/// Deliberately NEVER deletes user data — Extracted_Mods, Profiles, Library_Meta, Cache,
/// Pending_Downloads, Backups, settings.json, custom_skin.json, Logs, … are safe by construction
/// rather than by a preserve-list that would go stale every time the app grows a new data folder:
/// nothing inside any subfolder is ever touched, and at the install root only files matching
/// StaleRootFilePatterns (unambiguously the app's own program files — the exact set Uninstall.bat
/// already treats as app-owned) are removed, and only once the new build has fully landed. That
/// exception exists for the single-file packaging change (see IcarusStarlink.App.csproj): an
/// install upgrading from the old 336-file layout would otherwise keep ~320 dead runtime DLLs
/// forever — inert, but exactly the clutter that change was asked for to get rid of.
/// </summary>
public static class UpdateApplier
{
    /// <summary>The app's exe since 2.10.0, and the old name: a build has one or both (the old one is a launcher for a while).</summary>
    public const string AppExeName = "IcarusStarlink.exe";
    public const string OldAppExeName = "IcarusStarlink.App.exe";
    private const int CopyAttempts = 5;
    private const int CopyRetryDelayMs = 500;

    /// <summary>
    /// Root-level file shapes the app itself ships and nothing else ever puts there — a stale one
    /// (present in the install root, absent from the new build's root) is a leftover of a previous
    /// version's layout. createdump.exe is the one .exe among them: the old self-contained runtime's
    /// own crash-dump helper. Never a broader "*.exe" — the app's and updater's own exes are
    /// overwritten by the copy loop, and anything else is not ours to remove. The old layout's
    /// runtimes\ folder (per-RID native assets) is the one subfolder treated the same way, by exact
    /// name, since Uninstall.bat already lists it as regenerable app output.
    /// </summary>
    public static readonly string[] StaleRootFilePatterns = ["*.dll", "*.pdb", "*.xml", "*.deps.json", "*.runtimeconfig.json", "createdump.exe"];

    /// <summary>
    /// Returns the number of files copied. Throws when newFilesDirectory doesn't look like an app
    /// build at all (neither exe name) — the one guard against being pointed at the wrong folder.
    /// </summary>
    /// <param name="skipRootFiles">
    /// Root files of the new build not to copy: IcarusStarlink.App.exe once the app has moved the
    /// user over to IcarusStarlink.exe and removed it, so an update doesn't put it back.
    /// </param>
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
    public static int Apply(string installDirectory, string newFilesDirectory, Action<string> log,
        IReadOnlyCollection<string>? skipRootFiles = null)
    {
        if (!File.Exists(Path.Combine(newFilesDirectory, AppExeName)) && !File.Exists(Path.Combine(newFilesDirectory, OldAppExeName)))
        {
            throw new InvalidOperationException(
                $"'{newFilesDirectory}' doesn't contain {AppExeName} — refusing to copy something that isn't an app build.");
        }

        var skipped = new HashSet<string>(skipRootFiles ?? [], StringComparer.OrdinalIgnoreCase);

        var backupDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"UpdateBackup_{Guid.NewGuid():N}");
        var backedUpRelativePaths = new List<string>();

        try
        {
            var copied = 0;
            foreach (var sourcePath in Directory.GetFiles(newFilesDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(newFilesDirectory, sourcePath);
                if (skipped.Contains(relativePath))
                {
                    log($"Not copying {relativePath} (no longer used on this PC).");
                    continue;
                }

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
            RemoveStaleRootFiles(installDirectory, newFilesDirectory, log);
            TryDeleteBackupDirectory(backupDirectory, log);
            return copied;
        }
        catch (Exception ex)
        {
            log($"Update failed partway through ({ex.Message}) — rolling back {backedUpRelativePaths.Count} file(s) already overwritten.");
            if (RestoreBackup(installDirectory, backupDirectory, backedUpRelativePaths, log))
            {
                TryDeleteBackupDirectory(backupDirectory, log);
                throw;
            }

            // Rollback itself didn't fully succeed (often the SAME stuck lock that broke the
            // forward copy also blocks the backward restore-copy) — installDirectory may now be a
            // genuine mixed old/new state, not the clean "old install fully restored" outcome a
            // normal failed update produces. A distinct exception type so Program.cs (which runs
            // this with no visible window at all) can escalate to a user-visible notification only
            // for this specific, worse-than-usual outcome, rather than the user's only trace of it
            // being an easy-to-miss updater.log line.
            log($"Backup kept at '{backupDirectory}' for manual recovery — rollback couldn't restore every file.");
            throw new UpdateRollbackIncompleteException(
                $"Update failed and rollback couldn't fully restore the install directory: {ex.Message}", backupDirectory, ex);
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

    /// <summary>
    /// Runs only after every file of the new build has landed, so a failure partway through the
    /// copy loop (which rolls back) never reaches this — the install is already a complete,
    /// working new version by the time anything gets removed. Best-effort per file: a stale file
    /// that can't be deleted (locked by antivirus mid-scan, say) is logged and left, never turned
    /// into a failed update, since it's inert either way. See StaleRootFilePatterns for exactly
    /// what qualifies and why the class doc comment's never-delete-user-data promise still holds.
    /// </summary>
    private static void RemoveStaleRootFiles(string installDirectory, string newFilesDirectory, Action<string> log)
    {
        var shippedRootFiles = new HashSet<string>(
            Directory.GetFiles(newFilesDirectory).Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var pattern in StaleRootFilePatterns)
        {
            foreach (var stalePath in Directory.GetFiles(installDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                if (shippedRootFiles.Contains(Path.GetFileName(stalePath)))
                {
                    continue;
                }

                try
                {
                    TryClearReadOnly(stalePath);
                    File.Delete(stalePath);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log($"Couldn't remove stale '{Path.GetFileName(stalePath)}' ({ex.Message}) — leaving it; it's inert.");
                }
            }
        }

        var staleRuntimesDirectory = Path.Combine(installDirectory, "runtimes");
        if (Directory.Exists(staleRuntimesDirectory) && !Directory.Exists(Path.Combine(newFilesDirectory, "runtimes")))
        {
            try
            {
                Directory.Delete(staleRuntimesDirectory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Couldn't remove the stale runtimes\\ folder ({ex.Message}) — leaving it; it's inert.");
            }
        }

        if (removed > 0)
        {
            log($"Removed {removed} stale file(s)/folder(s) from a previous version's layout that the new build no longer ships.");
        }
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
