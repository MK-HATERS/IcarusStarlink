using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Server;

public sealed record ServerPakFile(string FileName, long SizeBytes, DateTime ModifiedLocal);

public sealed record ServerUe4ssMod(string Name, bool Enabled)
{
    /// <summary>"(on)" or "(off)", for the server's mod list.</summary>
    public string StateText => Enabled ? "  (on)" : "  (off)";
}

/// <summary>
/// The mod side of a local dedicated server: its Content\Paks\mods folder and its UE4SS install.
/// Every write here expects the server to be stopped (the caller checks) and never deletes: a pak
/// taken off the server is MOVED into the backups folder.
/// </summary>
public static class ServerModFiles
{
    public static IReadOnlyList<ServerPakFile> ListPaks(string installFolder)
    {
        var folder = DedicatedServerLayout.PakModsFolder(installFolder);
        return Directory.Exists(folder)
            ? [.. new DirectoryInfo(folder).GetFiles("*.pak").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new ServerPakFile(f.Name, f.Length, f.LastWriteTime))]
            : [];
    }

    /// <summary>The merged pak's earlier file name(s) (see PakIO's InstallManifestNames; Core can't reference PakIO).</summary>
    public static IReadOnlyList<string> LegacyMergedPakFileNames { get; } = ["ISL-Merged_P.pak", "ISL-Merged_9999_P.pak"];

    /// <summary>
    /// Copies a merged pak (and its sibling ISL-Merged.txt manifest, when present) into the server's
    /// mods folder, replacing an older copy of the same file. The merged pak under an earlier name
    /// is taken off the server too: moved into the removed-paks folder of <paramref name="backupsFolder"/> like
    /// RemovePak does, or deleted when no backups folder is given.
    /// </summary>
    public static void InstallPak(string sourcePakPath, string installFolder, string? manifestFileName, string? backupsFolder = null)
    {
        var target = DedicatedServerLayout.PakModsFolder(installFolder);
        Directory.CreateDirectory(target);
        File.Copy(sourcePakPath, Path.Combine(target, Path.GetFileName(sourcePakPath)), overwrite: true);

        // This app's merged pak under its earlier name: replaced by the one just copied.
        foreach (var legacy in LegacyMergedPakFileNames.Where(n => !string.Equals(n, Path.GetFileName(sourcePakPath), StringComparison.OrdinalIgnoreCase)))
        {
            var old = Path.Combine(target, legacy);
            if (!File.Exists(old))
            {
                continue;
            }

            if (backupsFolder is not null)
            {
                RemovePak(installFolder, legacy, backupsFolder);
            }
            else
            {
                File.Delete(old);
            }
        }

        if (manifestFileName is not null)
        {
            var manifest = Path.Combine(Path.GetDirectoryName(sourcePakPath)!, manifestFileName);
            if (File.Exists(manifest))
            {
                File.Copy(manifest, Path.Combine(target, manifestFileName), overwrite: true);
            }
        }
    }

    /// <summary>Moves a pak off the server into <paramref name="backupsFolder"/>\removed-paks (never a plain delete).</summary>
    public static string RemovePak(string installFolder, string pakFileName, string backupsFolder)
    {
        if (pakFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{pakFileName}' isn't a file name.", nameof(pakFileName));
        }

        var source = Path.Combine(DedicatedServerLayout.PakModsFolder(installFolder), pakFileName);
        var dest = Path.Combine(backupsFolder, "removed-paks", $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{pakFileName}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(source, dest);
        return dest;
    }

    /// <summary>UE4SS mod folders on the server, on when UE4SS starts them (mods.txt says 1, or the folder has an enabled.txt: Ue4ssModStart).</summary>
    public static IReadOnlyList<ServerUe4ssMod> ListUe4ssMods(string installFolder)
    {
        var mods = DedicatedServerLayout.Ue4ssModsFolder(installFolder);
        if (!Directory.Exists(mods))
        {
            return [];
        }

        var txt = Ue4ssModsTxt.Load(Path.Combine(mods, "mods.txt"));
        return [.. Directory.GetDirectories(mods)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => new ServerUe4ssMod(n!, Ue4ss.Ue4ssModStart.Starts(mods, n!, txt)))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Puts UE4SS on the server from the game client's own install (same game, same engine build —
    /// verified to load on the server through dwmapi.dll): dwmapi.dll, the loader's files
    /// (Ue4ssServerFiles.LoaderFiles: no logs, no Mods), and the framework's Mods\shared + Keybinds +
    /// mods.txt. Never overwrites a settings file or mods.txt the server already has. Returns false
    /// when the game has no UE4SS to copy.
    /// </summary>
    public static bool InstallUe4ssFromGame(string gameWin64Folder, string installFolder)
    {
        var gameUe4ss = Path.Combine(gameWin64Folder, "ue4ss");
        if (!File.Exists(Path.Combine(gameWin64Folder, "dwmapi.dll")) || !File.Exists(Path.Combine(gameUe4ss, "UE4SS.dll")))
        {
            return false;
        }

        var serverWin64 = DedicatedServerLayout.Win64Folder(installFolder);
        var serverUe4ss = Path.Combine(serverWin64, "ue4ss");
        Directory.CreateDirectory(Path.Combine(serverUe4ss, "Mods"));
        File.Copy(Path.Combine(gameWin64Folder, "dwmapi.dll"), Path.Combine(serverWin64, "dwmapi.dll"), overwrite: true);
        foreach (var relative in Ue4ssServerFiles.LoaderFiles(gameUe4ss))
        {
            var source = Path.Combine(gameUe4ss, relative);
            var target = Path.Combine(serverUe4ss, relative);
            if (string.Equals(relative, Ue4ssServerFiles.SettingsFileName, StringComparison.OrdinalIgnoreCase))
            {
                CopyIfMissing(source, target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        CopyIfMissing(Path.Combine(gameUe4ss, "Mods", "mods.txt"), Path.Combine(serverUe4ss, "Mods", "mods.txt"));
        foreach (var framework in new[] { "shared", "Keybinds" })
        {
            var source = Path.Combine(gameUe4ss, "Mods", framework);
            if (Directory.Exists(source))
            {
                Directories.Copy(source, Path.Combine(serverUe4ss, "Mods", framework));
            }
        }

        // The server now runs the game's build: drop its stale UE4SS.log and say which build that is.
        var (version, commit) = Ue4ssInstalledBuild.Read(gameUe4ss);
        Ue4ssInstalledBuild.RecordInstalled(serverUe4ss, version, commit);

        return true;
    }

    /// <summary>Moves a UE4SS mod folder off the server into the backups folder and drops its mods.txt line.</summary>
    public static void RemoveUe4ssMod(string installFolder, string modName, string backupsFolder)
    {
        RequireFolderName(modName);

        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(installFolder);
        var source = Path.Combine(serverMods, modName);
        if (Directory.Exists(source))
        {
            var dest = Path.Combine(backupsFolder, "removed-ue4ss", $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{modName}");
            Directories.Copy(source, dest);
            Directory.Delete(source, recursive: true);
        }

        var txtPath = Path.Combine(serverMods, "mods.txt");
        if (File.Exists(txtPath))
        {
            var txt = Ue4ssModsTxt.Load(txtPath);
            txt.Remove(modName);
            txt.Save(txtPath);
        }
    }

    /// <summary>"." and ".." pass the invalid-character check but name the Mods folder itself or its parent — deleting "the mod" would delete those.</summary>
    internal static void RequireFolderName(string modName)
    {
        if (string.IsNullOrWhiteSpace(modName) || modName.Trim('.').Length == 0 || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{modName}' isn't a folder name.", nameof(modName));
        }
    }

    private static void CopyIfMissing(string source, string target)
    {
        if (File.Exists(source) && !File.Exists(target))
        {
            File.Copy(source, target);
        }
    }
}
