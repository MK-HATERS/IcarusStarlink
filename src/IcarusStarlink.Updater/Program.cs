using System.Diagnostics;
using IcarusStarlink.Updater;

// IcarusStarlink's in-place updater (big-plan item 4). Launched by the main app from a TEMP copy
// of itself (so its own file in the install directory can be replaced too), with the main app
// exiting immediately after. Waits for the app to fully exit, copies the new build's files over
// the install directory (never deleting anything — see UpdateApplier), then relaunches the app.
//
// usage: IcarusStarlink.Updater.exe <mainProcessId> <installDir> <newFilesDir> <relaunchExe>

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: IcarusStarlink.Updater.exe <mainProcessId> <installDir> <newFilesDir> <relaunchExe>");
    return 2;
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
Directory.CreateDirectory(logDirectory);
var logPath = Path.Combine(logDirectory, "updater.log");

void Log(string message)
{
    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
    Console.WriteLine(line);
    try
    {
        File.AppendAllText(logPath, line + Environment.NewLine);
    }
    catch (IOException)
    {
        // Logging must never be the reason an update fails.
    }
}

Log($"Updater started — waiting for process {mainProcessId} to exit.");

try
{
    using var mainProcess = Process.GetProcessById(mainProcessId);
    if (!mainProcess.WaitForExit(60_000))
    {
        Log("The app didn't exit within 60 seconds — aborting, nothing was changed.");
        return 3;
    }
}
catch (ArgumentException)
{
    // Already exited before we could look it up — exactly the state we were waiting for.
}

// WaitForExit returning doesn't guarantee every file handle is released yet; a short grace
// period plus UpdateApplier's own per-file retry covers the stragglers.
Thread.Sleep(500);

bool Relaunch()
{
    try
    {
        Process.Start(new ProcessStartInfo(relaunchExePath) { WorkingDirectory = installDirectory, UseShellExecute = true });
        return true;
    }
    catch (Exception ex)
    {
        Log($"Relaunch failed ({ex.Message}) — start the app manually.");
        return false;
    }
}

var result = UpdateRunner.Run(installDirectory, newFilesDirectory, Log, Relaunch);

// This process runs with CreateNoWindow: true (see SettingsViewModel's own launch of it) — a real
// Win32 message box (not a console window; there is none here) is the only way for this headless
// process to make sure the user actually sees the one outcome that genuinely needs their attention:
// RollbackIncomplete, where the install directory may still be broken even after a best-effort
// relaunch attempt. Applied and RolledBack both stay silent by design (no alarming message box for
// a fully successful update or a self-healing failure) — Run's own relaunch call already covers the
// real bug this was fixed for (the app used to just close and never come back).
if (result.Outcome == UpdateRunner.Outcome.RollbackIncomplete)
{
    NativeMessageBox.Show(
        $"The IcarusStarlink update failed and could not be fully rolled back.\n\n"
        + $"A backup of the files that were overwritten is kept at:\n{result.BackupDirectory}\n\n"
        + $"Full details are in {logPath}",
        "IcarusStarlink update failed");
}

return result.Outcome switch
{
    UpdateRunner.Outcome.Applied => result.Relaunched ? 0 : 5,
    _ => 4,
};
