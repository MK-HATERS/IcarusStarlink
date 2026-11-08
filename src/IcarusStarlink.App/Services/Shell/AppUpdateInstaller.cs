using System.Diagnostics;
using System.IO;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Core;

namespace IcarusStarlink.App.Services;

/// <summary>The update window's steps, in order.</summary>
public enum AppUpdateStep { GetLink, Download, Check, Unpack, Handover }

/// <summary>
/// The file work of the in-app update, out of SettingsViewModel (which keeps the confirmation, the
/// busy checks and the status line): clears old work folders, downloads and unzips the release,
/// finds the app in it, stages the updater outside the install folder and starts it. The caller
/// then closes the app; the updater waits for that, copies the new build over the install
/// WITHOUT deleting anything (user data is safe by construction — see UpdateApplier), and
/// relaunches it.
/// </summary>
public static class AppUpdateInstaller
{
    /// <summary>
    /// A fresh work folder for one update (the zip and the unpacked build go in it). Earlier
    /// updates' work folders (zip + full extracted build, ~2x the release size each) were never
    /// cleaned up — by anyone, since the updater runs from one and the app never looked (an audit
    /// finding). Any previous AppUpdate_* folder is finished with by now.
    /// </summary>
    public static string NewWorkFolder()
    {
        var updateWorkRoot = Path.Combine(Path.GetTempPath(), "IcarusStarlink");
        if (Directory.Exists(updateWorkRoot))
        {
            foreach (var stale in Directory.GetDirectories(updateWorkRoot, "AppUpdate_*"))
            {
                try
                {
                    Directory.Delete(stale, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort; retried on the next update.
                }
            }
        }

        var workDirectory = Path.Combine(updateWorkRoot, $"AppUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        return workDirectory;
    }

    /// <summary>
    /// From a downloaded (and checked) release zip in <paramref name="workDirectory"/>: unpacks it,
    /// stages the updater and starts it. Null once the updater is running (close the app now),
    /// else why it stopped before anything was changed. Exceptions (a full disk) are the caller's.
    /// </summary>
    /// <param name="unpacking">Called when the unpacking starts and when the updater is being readied, for the progress window.</param>
    /// <param name="busyNow">Asked again right before the updater starts: the download took a while, and work may have started meanwhile.</param>
    /// <param name="skipOldExe">The user was moved over to IcarusStarlink.exe (AppSettings.ExeRenameDone): don't copy the old-name launcher back.</param>
    /// <param name="cancellationToken">The update window's Cancel: honoured until the updater is started (OperationCanceledException).</param>
    public static async Task<string?> InstallFromZipAsync(AppUpdateRelease release, string zipPath, string workDirectory, Action<AppUpdateStep> unpacking,
        Func<string?> busyNow, bool skipOldExe = false, CancellationToken cancellationToken = default)
    {
        unpacking(AppUpdateStep.Unpack);
        var extractDirectory = Path.Combine(workDirectory, "new");
        await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectoryAsync(zipPath, extractDirectory, cancellationToken), cancellationToken);

        // A Cancel that lands as the unpacking ends still stops it here.
        cancellationToken.ThrowIfCancellationRequested();

        // Tolerate a release zip that wraps everything in a single top-level folder — a common
        // way zips get built — as long as the app exe is findable one level down.
        // Either exe name counts (it was IcarusStarlink.App.exe before 3.0).
        static bool HasApp(string folder) =>
            File.Exists(Path.Combine(folder, StarlinkExe.FileName)) || File.Exists(Path.Combine(folder, StarlinkExe.OldFileName));

        var newFilesRoot = extractDirectory;
        if (!HasApp(newFilesRoot))
        {
            var subdirectories = Directory.GetDirectories(newFilesRoot);
            if (subdirectories.Length == 1 && HasApp(subdirectories[0]))
            {
                newFilesRoot = subdirectories[0];
            }
            else
            {
                return $"The downloaded release zip doesn't contain {StarlinkExe.FileName} — not installing it.";
            }
        }

        // A release from before the rename (the Stable channel can offer an older one on a beta
        // build) has only the old name. Once this PC was moved over, installing it would leave the
        // newer IcarusStarlink.exe its shortcuts open next to an older app.
        var releaseHasNewName = File.Exists(Path.Combine(newFilesRoot, StarlinkExe.FileName));
        if (skipOldExe && !releaseHasNewName)
        {
            return $"v{release.Version} is from before Starlink's exe was renamed, so it can't be installed over this version. "
                + "Download it from GitHub into a new folder if you need it.";
        }

        // The updater copies every file of the new build over the install: a build carrying
        // Starlink's own data would replace the user's.
        var userData = UserDataFiles.Where(name => File.Exists(Path.Combine(newFilesRoot, name)))
            .Concat(UserDataFolders.Where(name => Directory.Exists(Path.Combine(newFilesRoot, name))))
            .ToList();
        if (userData.Count > 0)
        {
            return $"The v{release.Version} download holds Starlink's own data ({string.Join(", ", userData)}), so installing it would replace yours. "
                + "Nothing was changed. Please report this on Starlink's Nexus page.";
        }

        unpacking(AppUpdateStep.Handover);

        // The updater must run from OUTSIDE the install folder or it couldn't replace its own
        // files: stage it into the temp work folder and launch from there. In a published
        // release the Updater is a single self-contained exe (see IcarusStarlink.App.csproj's
        // PublishSingleFileUpdater target for the real bug that forced that: staging the old
        // four-file self-contained layout without its runtime couldn't even start), so only
        // the .exe is required. A plain dev build still has the framework-dependent four-file
        // shape, so its three companions are staged too whenever they happen to be present.
        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var updaterStaging = Path.Combine(workDirectory, "updater");
        Directory.CreateDirectory(updaterStaging);
        var updaterExeSource = Path.Combine(installDirectory, "IcarusStarlink.Updater.exe");
        if (!File.Exists(updaterExeSource))
        {
            return "Couldn't stage the updater — IcarusStarlink.Updater.exe is missing next to the app.";
        }

        File.Copy(updaterExeSource, Path.Combine(updaterStaging, "IcarusStarlink.Updater.exe"));
        foreach (var companion in new[] { "IcarusStarlink.Updater.dll", "IcarusStarlink.Updater.runtimeconfig.json", "IcarusStarlink.Updater.deps.json" })
        {
            var source = Path.Combine(installDirectory, companion);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(updaterStaging, companion));
            }
        }

        // Looked at again: the download took a while, and server work may have started meanwhile.
        if (busyNow() is { } startedMeanwhile)
        {
            return startedMeanwhile;
        }

        // Started again under the name the release itself uses (an older release only has the old one).
        var appExePath = Path.Combine(installDirectory, releaseHasNewName ? StarlinkExe.FileName : StarlinkExe.OldFileName);
        var updater = new ProcessStartInfo(Path.Combine(updaterStaging, "IcarusStarlink.Updater.exe"))
        {
            ArgumentList = { Environment.ProcessId.ToString(), installDirectory, newFilesRoot, appExePath },
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Once the old exe name is gone from this PC, an update must not bring it back.
        if (skipOldExe)
        {
            updater.ArgumentList.Add("--skip");
            updater.ArgumentList.Add(StarlinkExe.OldFileName);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(updater);
        return null;
    }

    /// <summary>
    /// Names Starlink creates beside its exe for the user's data. A release never ships them, and
    /// a zip made from a folder the app has run in always has Logs and settings.json. A list of
    /// names rather than "no subfolders": the check runs in the version already installed, so it
    /// must never refuse a future release that adds a folder of its own.
    /// </summary>
    private static readonly string[] UserDataFiles = ["settings.json", "custom_skin.json", "find_broken_mod.json", "dedicated_servers.json", "ftp_sites.json"];

    private static readonly string[] UserDataFolders =
        ["Logs", "Extracted_Mods", "Library_Meta", "Profiles", "Profiles_Backups", "Pending_Downloads", "Staged_UE4SS", "Ue4ss_Meta", "PrebuiltPakSources"];
}
