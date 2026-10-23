using System.Diagnostics;
using System.IO;
using IcarusStarlink.Core;
using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Moves this PC over from IcarusStarlink.App.exe to IcarusStarlink.exe (the exe was renamed in
/// 2.10.0; the old name ships as a small launcher so older updaters and old shortcuts keep
/// working). Points the user's shortcuts (desktop, Start menu, taskbar pins) and the nxm:// handler
/// at the new exe, then deletes the old one and checks it is really gone. The app records
/// AppSettings.ExeRenameDone only when every step worked, so a failed step is tried again at the
/// next start, and from then on the in-app update leaves the launcher out (AppUpdateInstaller).
/// </summary>
public sealed class ExeRenameMigration(
    string installDirectory, IShortcutEditor shortcuts, INxmProtocolRegistrar nxm, IReadOnlyList<ShortcutFolder> shortcutFolders)
{
    public string OldExePath { get; } = Path.Combine(installDirectory, StarlinkExe.OldFileName);

    public string NewExePath { get; } = Path.Combine(installDirectory, StarlinkExe.FileName);

    /// <summary>Waits this long for the old launcher to exit (it starts this app and closes straight away).</summary>
    public TimeSpan LauncherExitWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>What there is to do; reads every shortcut, so call it off the UI thread.</summary>
    public ExeRenamePlan Plan()
    {
        var found = new List<string>();
        foreach (var folder in shortcutFolders)
        {
            foreach (var link in ShortcutsIn(folder))
            {
                if (SamePath(shortcuts.TryReadTarget(link), OldExePath))
                {
                    found.Add(link);
                }
            }
        }

        var nxmCommand = TryRead(nxm.CurrentHandlerCommand);
        return new ExeRenamePlan(found, SamePath(NxmHandlerCommand.ExePathOf(nxmCommand), OldExePath), File.Exists(OldExePath));
    }

    /// <summary>
    /// Every step of the plan, each one checked afterwards. The old exe is only deleted when every
    /// shortcut was moved: deleting it first would break a shortcut that couldn't be.
    /// </summary>
    public IReadOnlyList<ExeRenameStep> Run(ExeRenamePlan plan)
    {
        var steps = new List<ExeRenameStep>();
        foreach (var link in plan.Shortcuts)
        {
            var name = Path.GetFileNameWithoutExtension(link);
            try
            {
                shortcuts.Repoint(link, OldExePath, NewExePath);
                steps.Add(SamePath(shortcuts.TryReadTarget(link), NewExePath)
                    ? new ExeRenameStep($"Shortcut \"{name}\" now opens {StarlinkExe.FileName}", true)
                    : new ExeRenameStep($"Shortcut \"{name}\" still points at the old exe", false, link));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                steps.Add(new ExeRenameStep($"Couldn't change shortcut \"{name}\": {ex.Message}", false,
                    $"{link}\nRight-click it › Properties and set the target to {NewExePath}, or delete it."));
            }
        }

        if (plan.NxmUsesOldExe)
        {
            try
            {
                nxm.Register();
                steps.Add(nxm.IsRegisteredToThisApp()
                    ? new ExeRenameStep($"Nexus \"Mod Manager Download\" links now open {StarlinkExe.FileName}", true)
                    : new ExeRenameStep("Nexus links still open the old exe", false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                steps.Add(new ExeRenameStep($"Couldn't move Nexus links over: {ex.Message}", false));
            }
        }

        if (steps.Any(step => !step.Succeeded))
        {
            if (File.Exists(OldExePath))
            {
                steps.Add(new ExeRenameStep($"Kept {StarlinkExe.OldFileName} for now, so nothing above stops working", false));
            }

            return steps;
        }

        if (File.Exists(OldExePath))
        {
            var problem = DeleteOldExe();
            steps.Add(problem is null && !File.Exists(OldExePath)
                ? new ExeRenameStep($"Deleted {StarlinkExe.OldFileName} (checked: it's gone)", true)
                : new ExeRenameStep($"Couldn't delete {StarlinkExe.OldFileName}: {problem ?? "it's still there"}", false, OldExePath));
        }
        else if (plan.OldExeExists)
        {
            steps.Add(new ExeRenameStep($"{StarlinkExe.OldFileName} is already gone", true));
        }

        return steps;
    }

    /// <summary>Null once deleted; else why not.</summary>
    private string? DeleteOldExe()
    {
        WaitForLauncherToExit();
        string? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(OldExePath))
                {
                    File.SetAttributes(OldExePath, FileAttributes.Normal);
                    File.Delete(OldExePath);
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Antivirus scanning it, or the launcher still closing.
                last = ex.Message;
                Thread.Sleep(400);
            }
        }

        return last;
    }

    private void WaitForLauncherToExit()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(StarlinkExe.OldFileName)))
        {
            using (process)
            {
                try
                {
                    if (SamePath(process.MainModule?.FileName, OldExePath))
                    {
                        process.WaitForExit(LauncherExitWait);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited meanwhile, or not ours to look at.
                }
            }
        }
    }

    private static IEnumerable<string> ShortcutsIn(ShortcutFolder folder)
    {
        if (string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(folder.Path, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = folder.Recursive,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryRead(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(a.Trim()), Path.GetFullPath(b.Trim()), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Where Windows keeps the user's shortcuts: desktop, Start menu, taskbar and Start pins, for this user and for all users.</summary>
    public static IReadOnlyList<ShortcutFolder> WindowsShortcutFolders()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
            new(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true),
            new(Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch"), true),
        ];
    }
}

/// <summary>A folder to look for shortcuts in; Recursive for the Start menu's own subfolders.</summary>
public sealed record ShortcutFolder(string Path, bool Recursive);

/// <summary>What the move-over would change. Nothing to do: no old exe, shortcut or nxm:// handler left.</summary>
public sealed record ExeRenamePlan(IReadOnlyList<string> Shortcuts, bool NxmUsesOldExe, bool OldExeExists)
{
    public bool HasWork => Shortcuts.Count > 0 || NxmUsesOldExe || OldExeExists;
}

/// <summary>One line of the result; Detail is where to look when it failed.</summary>
public sealed record ExeRenameStep(string Text, bool Succeeded, string? Detail = null);

/// <summary>Reads and changes .lnk files (WScript.Shell on Windows; a fake in tests).</summary>
public interface IShortcutEditor
{
    /// <summary>The exe the shortcut opens; null when it can't be read.</summary>
    string? TryReadTarget(string shortcutPath);

    /// <summary>Points the shortcut at newTarget, and its icon too when that was taken from oldTarget.</summary>
    void Repoint(string shortcutPath, string oldTarget, string newTarget);
}
