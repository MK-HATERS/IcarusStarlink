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

    /// <summary>Returns the number of files copied. Throws when newFilesDirectory doesn't look like an app build at all (no IcarusStarlink.App.exe) — the one guard against being pointed at the wrong folder.</summary>
    public static int Apply(string installDirectory, string newFilesDirectory, Action<string> log)
    {
        if (!File.Exists(Path.Combine(newFilesDirectory, AppExeName)))
        {
            throw new InvalidOperationException(
                $"'{newFilesDirectory}' doesn't contain {AppExeName} — refusing to copy something that isn't an app build.");
        }

        var copied = 0;
        foreach (var sourcePath in Directory.GetFiles(newFilesDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(newFilesDirectory, sourcePath);
            var destinationPath = Path.Combine(installDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            CopyWithRetry(sourcePath, destinationPath, log);
            copied++;
        }

        log($"Copied {copied} file(s) from '{newFilesDirectory}' into '{installDirectory}'.");
        return copied;
    }

    /// <summary>The just-exited main process can hold file handles for a moment after Process.WaitForExit returns (antivirus scans stretch this further) — a short retry loop covers that instead of failing the whole update on the first sharing violation.</summary>
    private static void CopyWithRetry(string sourcePath, string destinationPath, Action<string> log)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException ex) when (attempt < CopyAttempts)
            {
                log($"Attempt {attempt} to copy '{destinationPath}' failed ({ex.Message}) — retrying.");
                Thread.Sleep(CopyRetryDelayMs);
            }
        }
    }
}
