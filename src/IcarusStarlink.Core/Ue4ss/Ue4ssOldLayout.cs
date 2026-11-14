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

    /// <summary>The old setup in this Binaries\Win64 folder; null when there is none. Reads only.</summary>
    public static Found? Find(string win64Folder)
    {
        try
        {
            var modsFolder = Path.Combine(win64Folder, "Mods");
            var hasDll = File.Exists(Path.Combine(win64Folder, "UE4SS.dll"));
            if (!hasDll && !File.Exists(Path.Combine(modsFolder, "mods.txt")))
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
    /// follows brings the new ones. A step that fails undoes the ones before it and throws.
    /// Does nothing when there is no old setup.
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
                        Directory.Delete(folder, recursive: true);
                        moved.Did(() => Directories.Copy(Path.Combine(backup, "Mods", name), folder));
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
                        File.Delete(file);
                        moved.Did(() => File.Copy(Path.Combine(backup, "Mods", name), file, overwrite: true));
                    }
                    else
                    {
                        MoveFileIfFree(file, Path.Combine(newMods, name), moved);
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
                if (File.Exists(file))
                {
                    MoveFileIfFree(file, Path.Combine(newRoot, name), moved);
                }
            }

            foreach (var name in PlayerFolders)
            {
                var folder = Path.Combine(win64Folder, name);
                var target = Path.Combine(newRoot, name);
                if (Directory.Exists(folder) && !Directory.Exists(target))
                {
                    Directory.Move(folder, target);
                    moved.Did(() => Directory.Move(target, folder));
                }
            }

            var oldDll = Path.Combine(win64Folder, "UE4SS.dll");
            if (File.Exists(oldDll))
            {
                File.Delete(oldDll);
                moved.Did(() => File.Copy(Path.Combine(backup, "UE4SS.dll"), oldDll, overwrite: true));
            }

            return moved;
        }
        catch (Exception)
        {
            moved.Undo();
            throw;
        }
    }

    private static void MoveFileIfFree(string file, string target, Moved moved)
    {
        if (File.Exists(target) || Directory.Exists(target))
        {
            return;
        }

        File.Move(file, target);
        moved.Did(() => File.Move(target, file));
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
