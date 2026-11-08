using System.Diagnostics;
using IcarusStarlink.Updater;

// Icarus Starlink's in-place updater. Started by the main app (AppUpdateInstaller) from a TEMP copy
// of itself (so its own file in the install directory can be replaced too), with the main app
// exiting right after. Waits for the app to exit, copies the new build's files over the install
// directory (never deleting user data, only the old build's leftover program files — see
// UpdateApplier), then relaunches the app.
//
// usage: IcarusStarlink.Updater.exe <mainProcessId> <installDir> <newFilesDir> <relaunchExe> [--skip <rootFileName>]...

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: IcarusStarlink.Updater.exe <mainProcessId> <installDir> <newFilesDir> <relaunchExe> [--skip <rootFileName>]...");
    return 2;
}

// Root files of the new build not to copy (the old exe name, once the app has moved the user off it).
var skipRootFiles = new List<string>();
for (var i = 4; i + 1 < args.Length; i++)
{
    if (args[i] == "--skip")
    {
        skipRootFiles.Add(args[++i]);
    }
}

if (!int.TryParse(args[0], out var mainProcessId))
{
    Console.Error.WriteLine($"'{args[0]}' isn't a process id.");
    return 2;
}

var installDirectory = Path.GetFullPath(args[1]);
var newFilesDirectory = Path.GetFullPath(args[2]);
var relaunchExePath = Path.GetFullPath(args[3]);

// Logged into the install's own Logs folder so a failed update is diagnosable from the same
// place every other app log already lives.
var logDirectory = Path.Combine(installDirectory, "Logs");
var logPath = Path.Combine(logDirectory, "updater.log");
try
{
    Directory.CreateDirectory(logDirectory);
}
catch (Exception)
{
    // A read-only/ACL-denied Logs folder must not kill the updater before it even starts (that
    // was one more way for "the app closed and never came back") — log to TEMP instead.
    logPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink-updater.log");
}

void Log(string message)
{
    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
    Console.WriteLine(line);
    try
    {
        File.AppendAllText(logPath, line + Environment.NewLine);
    }
    catch (Exception)
    {
        // Logging must never be the reason an update fails — any failure here, not just IOException
        // (UnauthorizedAccessException from a read-only updater.log used to escape mid-copy, trigger
        // a rollback, and skip the relaunch).
    }
}

Log($"Updater started — waiting for process {mainProcessId} to exit.");

// On screen while Starlink is closed (it used to vanish for a few seconds with nothing showing).
using var progressWindow = new NativeProgressWindow("Updating Icarus Starlink", "Waiting for Icarus Starlink to close…");

bool Relaunch()
{
    progressWindow.SetText("Starting Icarus Starlink…");
    try
    {
        // The exe was renamed in 3.0: whichever name the install has now, if the given one isn't there.
        var exe = relaunchExePath;
        if (!File.Exists(exe))
        {
            exe = new[] { UpdateApplier.AppExeName, UpdateApplier.OldAppExeName }
                .Select(name => Path.Combine(installDirectory, name))
                .FirstOrDefault(File.Exists) ?? exe;
        }

        // Closed as Starlink starts: its own splash screen takes over (both showing at once looked
        // like two windows doing the same thing).
        progressWindow.Dispose();
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDirectory, UseShellExecute = true });
        return true;
    }
    catch (Exception ex)
    {
        Log($"Relaunch failed ({ex.Message}) — start the app manually.");
        return false;
    }
}

// Left for the relaunched app to read, so a rolled-back update is reported instead of silently
// bringing back the old version (see UpdateOutcomeFile).
void RecordOutcome(UpdateRunner.Outcome outcome, string message)
{
    var inLogs = Path.Combine(logDirectory, UpdateOutcomeFile.FileName);
    try
    {
        UpdateOutcomeFile.Write(inLogs, outcome, $"{message} Details: {logPath}");
    }
    catch (Exception)
    {
        UpdateOutcomeFile.Write(Path.Combine(Path.GetTempPath(), UpdateOutcomeFile.TempFileName), outcome, $"{message} Details: {logPath}");
    }
}

// Three minutes, not one: a slow shutdown (a big Library, a busy disk) used to run past 60 seconds,
// and then the updater gave up without a word or a relaunch.
var exitWait = TimeSpan.FromMinutes(3);
try
{
    using var mainProcess = Process.GetProcessById(mainProcessId);
    if (!mainProcess.WaitForExit(TimeSpan.FromSeconds(60)))
    {
        Log("The app hasn't exited after 60 seconds — still waiting.");
        if (!mainProcess.WaitForExit(exitWait - TimeSpan.FromSeconds(60)))
        {
            UpdateRunner.GiveUpWaiting(exitWait, Log, Relaunch, RecordOutcome);
            return 3;
        }
    }
}
catch (ArgumentException)
{
    // Already exited before we could look it up — exactly the state we were waiting for.
}

// WaitForExit returning doesn't guarantee every file handle is released yet; a short grace
// period plus UpdateApplier's own per-file retry covers the stragglers.
progressWindow.SetText("Installing the update…");
Thread.Sleep(500);

UpdateRunner.Result result;
try
{
    result = UpdateRunner.Run(installDirectory, newFilesDirectory, Log, Relaunch, RecordOutcome, skipRootFiles);
}
catch (Exception ex)
{
    // UpdateRunner.Run already handles every expected outcome; this is the last line of defense for
    // anything unexpected, and the one promise that must survive it is "the app comes back".
    Log($"Updater hit an unexpected error ({ex.Message}) — relaunching the app as it is.");
    Relaunch();
    return 1;
}

// AppUpdateInstaller starts this exe with CreateNoWindow (no console). Its only window is
// NativeProgressWindow, closed as the app restarts. RollbackIncomplete is the one outcome that also
// gets a message box, because the install may be left broken; Applied and RolledBack are reported
// by the relaunched app (UpdateOutcomeFile).
if (result.Outcome == UpdateRunner.Outcome.RollbackIncomplete)
{
    progressWindow.Dispose();
    NativeMessageBox.Show(
        "The Icarus Starlink update failed and couldn't be fully undone, so some of its files may be missing.\n\n"
        + $"To fix it, download Icarus Starlink again from GitHub or Nexus and unzip it over:\n{installDirectory}\nYour mods and settings stay.\n\n"
        + $"The files the update replaced are kept in:\n{result.BackupDirectory}\n\n"
        + $"Details: {logPath}",
        "Icarus Starlink update failed");
}

return result.Outcome switch
{
    UpdateRunner.Outcome.Applied => result.Relaunched ? 0 : 5,
    _ => 4,
};
