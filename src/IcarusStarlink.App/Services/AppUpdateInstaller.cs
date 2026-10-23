using System.Diagnostics;
using System.IO;
using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The file work of the in-app update, out of SettingsViewModel (which keeps the confirmation, the
/// busy checks and the status line): clears old work folders, downloads and unzips the release,
/// finds the app in it, stages the updater outside the install folder and starts it. The caller
/// then closes the app; the updater waits for that, copies the new build over the install
/// WITHOUT deleting anything (user data is safe by construction — see UpdateApplier), and
/// relaunches it.
/// </summary>
public sealed class AppUpdateInstaller(IAppUpdateClient appUpdateClient)
{
    /// <summary>
    /// Null once the updater is running (close the app now), else why it stopped before anything
    /// was changed. Exceptions (a failed download, a full disk) are the caller's to report.
    /// </summary>
    /// <param name="status">Each step for the status line ("Downloading v2.10.0…").</param>
    /// <param name="busyNow">Asked again right before the updater starts: the download took a while, and work may have started meanwhile.</param>
    public async Task<string?> StartAsync(AppUpdateRelease release, Action<string> status, Func<string?> busyNow)
    {
        // Earlier updates' work folders (zip + full extracted build, ~2x the release size each)
        // were never cleaned up — by anyone, since the updater runs from one and the app never
        // looked (an audit finding). Any previous AppUpdate_* folder is finished with by now.
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

        status($"Downloading v{release.Version}…");
        var zipPath = Path.Combine(workDirectory, "update.zip");
        await appUpdateClient.DownloadAssetAsync(release, zipPath);

        status("Preparing update…");
        var extractDirectory = Path.Combine(workDirectory, "new");
        await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDirectory));

        // Tolerate a release zip that wraps everything in a single top-level folder — a common
        // way zips get built — as long as the app exe is findable one level down.
        var newFilesRoot = extractDirectory;
        if (!File.Exists(Path.Combine(newFilesRoot, "IcarusStarlink.App.exe")))
        {
            var subdirectories = Directory.GetDirectories(newFilesRoot);
            if (subdirectories.Length == 1 && File.Exists(Path.Combine(subdirectories[0], "IcarusStarlink.App.exe")))
            {
                newFilesRoot = subdirectories[0];
            }
            else
            {
                return "The downloaded release zip doesn't contain IcarusStarlink.App.exe — not installing it.";
            }
        }

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

        var appExePath = Path.Combine(installDirectory, "IcarusStarlink.App.exe");
        Process.Start(new ProcessStartInfo(Path.Combine(updaterStaging, "IcarusStarlink.Updater.exe"))
        {
            ArgumentList = { Environment.ProcessId.ToString(), installDirectory, newFilesRoot, appExePath },
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        return null;
    }
}
