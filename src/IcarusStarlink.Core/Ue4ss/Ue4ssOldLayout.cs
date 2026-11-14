using IcarusStarlink.Core.IO;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// The old way UE4SS was set up (the official 3.0.1 zip unpacked by hand): UE4SS.dll, Mods\,
/// Mods\mods.txt and UE4SS-settings.ini directly in Binaries\Win64. UE4SS 4 keeps only dwmapi.dll
/// there and looks for everything else in Binaries\Win64\ue4ss, which is where Starlink has always
/// put it. This finds an old setup and moves the player's part of it (their mods, which ones are
/// on, their settings) into the ue4ss folder, backed up first, and can put it all back.
/// </summary>
public static class Ue4ssOldLayout
{
    /// <summary>Settings and game-specific files a player may have changed, moved along with the mods when the ue4ss folder has none.</summary>
    private static readonly string[] PlayerFiles = [Ue4ssServerFiles.SettingsFileName, "VTableLayout.ini", "MemberVariableLayout.ini"];

    private static readonly string[] PlayerFolders = ["UE4SS_Signatures"];

    /// <param name="UserMods">The player's own mods in the old Mods folder (not UE4SS's built-ins), by folder name.</param>
    /// <param name="NativeMods">Those of them that are C++ mods (dlls\main.dll).</param>
    /// <param name="HasSettings">An old UE4SS-settings.ini is there.</param>
    /// <param name="Version">The version the old UE4SS.log names; null when it never ran or the log is gone.</param>
    public sealed record Found(
        string Win64Folder, IReadOnlyList<string> UserMods, IReadOnlyList<string> NativeMods, bool HasSettings, string? Version, string? Commit);

    /// <summary>
    /// The old setup in this Binaries\Win64 folder; null when there is none. Reads only. UE4SS.dll
    /// is what makes it one: a Mods folder alone (what a move leaves behind when the ue4ss folder
    /// already had a mod of the same name) is not a setup to move again.
    /// </summary>
    public static Found? Find(string win64Folder)
    {
        try
        {
            var modsFolder = Path.Combine(win64Folder, "Mods");
            if (!File.Exists(Path.Combine(win64Folder, "UE4SS.dll")))
            {
                return null;
            }

            var userMods = new List<string>();
            var nativeMods = new List<string>();
            if (Directory.Exists(modsFolder))
            {
                var framework = Ue4ssFrameworkMods.In(modsFolder);
                foreach (var folder in Directory.GetDirectories(modsFolder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(folder);
                    if (framework.Contains(name))
                    {
                        continue;
                    }

                    userMods.Add(name);
                    if (File.Exists(Path.Combine(folder, "dlls", "main.dll")))
                    {
                        nativeMods.Add(name);
                    }
                }
            }

            var (version, commit) = Ue4ssInstalledBuild.Read(win64Folder);
            return new Found(win64Folder, userMods, nativeMods, File.Exists(Path.Combine(win64Folder, Ue4ssServerFiles.SettingsFileName)), version, commit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>What a move did, for the caller to tell the player. <see cref="None"/> when there was no old setup.</summary>
    /// <param name="Mods">The player's mods now in ue4ss\Mods.</param>
    /// <param name="LeftInPlace">The player's mods still in Binaries\Win64\Mods: ue4ss\Mods already has one of that name.</param>
    /// <param name="LeftFiles">Files and folders still where they were for the same reason, as paths under Binaries\Win64 ("Mods\mods.txt", "UE4SS-settings.ini").</param>
    /// <param name="MovedSettings">The old UE4SS-settings.ini is now the ue4ss folder's.</param>
    /// <param name="BackupFolder">The copy of the old setup made before anything moved; null when nothing changed.</param>
    public sealed record Outcome(
        IReadOnlyList<string> Mods, IReadOnlyList<string> LeftInPlace, IReadOnlyList<string> LeftFiles, bool MovedSettings, string? BackupFolder)
    {
        public static Outcome None { get; } = new([], [], [], false, null);

        public bool Changed => BackupFolder is not null;

        /// <summary>
        /// The move in plain words for a status line, starting with a space; empty when nothing
        /// changed. Says what is now in the ue4ss folder, what stayed in Binaries\Win64 and why,
        /// and where the backup of the old setup (UE4SS's own old mods included) is.
        /// </summary>
        public string Describe()
        {
            if (!Changed)
            {
                return "";
            }

            var movedParts = new List<string>();
            if (Mods.Count > 0)
            {
                movedParts.Add(Mods.Count == 1 ? "Your mod" : $"Your {Mods.Count} mods");
            }

            if (MovedSettings)
            {
                movedParts.Add(movedParts.Count == 0 ? "Your UE4SS settings" : "your UE4SS settings");
            }

            var text = "";
            if (movedParts.Count > 0)
            {
                var verb = movedParts.Count == 1 && Mods.Count == 1 ? "is" : "are";
                text = $" {string.Join(" and ", movedParts)} {verb} now in the ue4ss folder.";
            }

            var left = LeftInPlace.Select(name => $@"Mods\{name}").Concat(LeftFiles).ToList();
            if (left.Count > 0)
            {
                text += $@" Left in Binaries\Win64, because the ue4ss folder already has one of that name: {string.Join(", ", left)}.";
            }

            return text + $" UE4SS's own old mods (Keybinds and the like) were replaced by the new build's. The old setup is backed up in {BackupFolder}.";
        }
    }

    /// <summary>What <see cref="MoveIntoUe4ssFolder"/> did, and the way back.</summary>
    public sealed class Moved
    {
        private readonly List<Action> _undo = [];

        internal Moved(string? backupFolder) => BackupFolder = backupFolder;

        /// <summary>False when there was no old setup: nothing was touched.</summary>
        public bool Changed => BackupFolder is not null;

        /// <summary>The copy of the old setup made before anything moved; null when nothing changed.</summary>
        public string? BackupFolder { get; }

        /// <summary>The player's mods now in ue4ss\Mods.</summary>
        public List<string> Mods { get; } = [];

        /// <summary>Mods left where they were because ue4ss\Mods already has a folder of that name.</summary>
        public List<string> LeftInPlace { get; } = [];

        /// <summary>Files and folders left where they were for the same reason, as paths under Binaries\Win64.</summary>
        public List<string> LeftFiles { get; } = [];

        /// <summary>The old UE4SS-settings.ini moved into the ue4ss folder.</summary>
        public bool MovedSettings { get; internal set; }

        /// <summary>What happened, without the way back.</summary>
        public Outcome ToOutcome() => Changed ? new Outcome([.. Mods], [.. LeftInPlace], [.. LeftFiles], MovedSettings, BackupFolder) : Outcome.None;

        internal void Did(Action undo) => _undo.Add(undo);

        /// <summary>Puts everything back where it was, newest step first. Returns what couldn't be put back (it is still in <see cref="BackupFolder"/>).</summary>
        public IReadOnlyList<string> Undo()
        {
            var failed = new List<string>();
            for (var i = _undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    _undo[i]();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(ex.Message);
                }
            }

            _undo.Clear();
            return failed;
        }
    }

    /// <summary>
    /// Moves an old setup into Binaries\Win64\ue4ss. First the whole old setup is copied to
    /// <paramref name="backupDirectory"/>\old-layout_(time). Then the player's mods, mods.txt (which
    /// mods are on; each mod's own enabled.txt travels inside its folder) and UE4SS-settings.ini
    /// move across, never over something the ue4ss folder already has. UE4SS's own old files
    /// (UE4SS.dll, its built-in mods) are taken out: they are in the backup, and the install that
    /// follows brings the new ones. What the ue4ss folder already has a namesake of stays in
    /// Binaries\Win64 and is named in the result. A step that fails undoes the ones before it, and
    /// its own half-done delete, and throws. Does nothing when there is no old setup.
    /// </summary>
    public static Moved MoveIntoUe4ssFolder(string win64Folder, string backupDirectory)
    {
        if (Find(win64Folder) is null)
        {
            return new Moved(null);
        }

        var oldMods = Path.Combine(win64Folder, "Mods");
        var newRoot = Path.Combine(win64Folder, "ue4ss");
        var newMods = Path.Combine(newRoot, "Mods");
        var backup = BackUp(win64Folder, backupDirectory);
        var moved = new Moved(backup);
        try
        {
            Directory.CreateDirectory(newRoot);
            if (Directory.Exists(oldMods))
            {
                Directory.CreateDirectory(newMods);
                var framework = Ue4ssFrameworkMods.In(oldMods);
                foreach (var folder in Directory.GetDirectories(oldMods))
                {
                    var name = Path.GetFileName(folder);
                    var target = Path.Combine(newMods, name);
                    var isShared = name.Equals("shared", StringComparison.OrdinalIgnoreCase);
                    if (framework.Contains(name) && !isShared)
                    {
                        // One of UE4SS's own built-in mods: the new build brings its own copy.
                        // The way back is noted first, so a delete that stops halfway is put back too.
                        moved.Did(() => RestoreMissing(Path.Combine(backup, "Mods", name), folder));
                        Directory.Delete(folder, recursive: true);
                    }
                    else if (Directory.Exists(target) || File.Exists(target))
                    {
                        if (!isShared)
                        {
                            moved.LeftInPlace.Add(name);
                        }
                    }
                    else
                    {
                        // Mods\shared (the Lua library) moves like a mod: a player may keep their own
                        // library files in it, and the install replaces only UE4SS's files there.
                        Directory.Move(folder, target);
                        moved.Did(() => Directory.Move(target, folder));
                        if (!isShared)
                        {
                            moved.Mods.Add(name);
                        }
                    }
                }

                foreach (var file in Directory.GetFiles(oldMods))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("mods.json", StringComparison.OrdinalIgnoreCase))
                    {
                        // The old build's list of its own built-in mods: stale beside a new build.
                        moved.Did(() => File.Copy(Path.Combine(backup, "Mods", name), file, overwrite: true));
                        File.Delete(file);
                    }
                    else if (!MoveFileIfFree(file, Path.Combine(newMods, name), moved))
                    {
                        moved.LeftFiles.Add($@"Mods\{name}");
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(oldMods).Any())
                {
                    Directory.Delete(oldMods);
                    moved.Did(() => Directory.CreateDirectory(oldMods));
                }
            }

            foreach (var name in PlayerFiles)
            {
                var file = Path.Combine(win64Folder, name);
                if (!File.Exists(file))
                {
                    continue;
                }

                if (!MoveFileIfFree(file, Path.Combine(newRoot, name), moved))
                {
                    moved.LeftFiles.Add(name);
                }
                else if (name == Ue4ssServerFiles.SettingsFileName)
                {
                    moved.MovedSettings = true;
                }
            }

            foreach (var name in PlayerFolders)
            {
                var folder = Path.Combine(win64Folder, name);
                var target = Path.Combine(newRoot, name);
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                if (Directory.Exists(target))
                {
                    moved.LeftFiles.Add(name);
                }
                else
                {
                    Directory.Move(folder, target);
                    moved.Did(() => Directory.Move(target, folder));
                }
            }

            var oldDll = Path.Combine(win64Folder, "UE4SS.dll");
            if (File.Exists(oldDll))
            {
                moved.Did(() => File.Copy(Path.Combine(backup, "UE4SS.dll"), oldDll, overwrite: true));
                File.Delete(oldDll);
            }

            return moved;
        }
        catch (Exception)
        {
            moved.Undo();
            throw;
        }
    }

    /// <summary>
    /// Copies back from the backup every file the target no longer has. A file still there
    /// survived the delete whole (and may be the open one that stopped it), so it is left alone.
    /// </summary>
    private static void RestoreMissing(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(dest))
            {
                File.Copy(file, dest);
            }
        }

        foreach (var folder in Directory.GetDirectories(source))
        {
            RestoreMissing(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    /// <summary>False when the target is taken: the file stays where it is.</summary>
    private static bool MoveFileIfFree(string file, string target, Moved moved)
    {
        if (File.Exists(target) || Directory.Exists(target))
        {
            return false;
        }

        File.Move(file, target);
        moved.Did(() => File.Move(target, file));
        return true;
    }

    /// <summary>Copies the old setup (UE4SS.dll, the player's files and the whole Mods folder) to a new folder and returns it.</summary>
    private static string BackUp(string win64Folder, string backupDirectory)
    {
        var stamp = $"old-layout_{DateTime.Now:yyyyMMdd-HHmmss}";
        var backup = Path.Combine(backupDirectory, stamp);
        for (var n = 2; Directory.Exists(backup); n++)
        {
            backup = Path.Combine(backupDirectory, $"{stamp}_{n}");
        }

        Directory.CreateDirectory(backup);
        foreach (var name in PlayerFiles.Append("UE4SS.dll"))
        {
            var file = Path.Combine(win64Folder, name);
            if (File.Exists(file))
            {
                File.Copy(file, Path.Combine(backup, name));
            }
        }

        foreach (var name in PlayerFolders.Append("Mods"))
        {
            var folder = Path.Combine(win64Folder, name);
            if (Directory.Exists(folder))
            {
                Directories.Copy(folder, Path.Combine(backup, name));
            }
        }

        return backup;
    }
}
