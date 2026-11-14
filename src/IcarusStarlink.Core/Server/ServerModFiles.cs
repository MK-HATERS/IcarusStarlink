using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Server;

public sealed record ServerPakFile(string FileName, long SizeBytes, DateTime ModifiedLocal);

/// <summary>
/// The mod side of a local dedicated server: its Content\Paks\mods folder and its UE4SS install.
/// Every write here expects the server to be stopped (the caller checks) and never deletes a mod: a
/// pak taken off the server is MOVED into the backups folder, and a merged pak that is replaced is
/// kept there first (only a mod list the new build doesn't have is deleted).
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

    /// <summary>
    /// Copies a merged pak (and its sibling <paramref name="manifestFileName"/> mod list) into the
    /// server's mods folder as <paramref name="targetFileName"/> (the source's own name when null).
    /// Each file is written through a temp file and swapped in whole, so a copy that fails part way
    /// leaves the server's working pak as it was. With a <paramref name="backupsFolder"/>, the
    /// server's copy being replaced (and its mod list) is kept in its removed-paks folder first,
    /// unless it is the same file as the new one. A build without a mod list removes the server's
    /// old one, which would otherwise be read as the new build's. The merged pak under any other of
    /// its names (MergedPakNames.All) is taken off the server too: moved into removed-paks like
    /// RemovePak does, or deleted when no backups folder is given.
    /// </summary>
    public static void InstallPak(string sourcePakPath, string installFolder, string? manifestFileName, string? backupsFolder = null, string? targetFileName = null)
    {
        var target = DedicatedServerLayout.PakModsFolder(installFolder);
        Directory.CreateDirectory(target);
        var targetName = targetFileName ?? Path.GetFileName(sourcePakPath);
        var targetPak = Path.Combine(target, targetName);
        if (backupsFolder is not null && File.Exists(targetPak) && !ServerUe4ssSync.SamePak(sourcePakPath, targetPak))
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var removed = Path.Combine(backupsFolder, "removed-paks");
            Directory.CreateDirectory(removed);
            File.Copy(targetPak, Path.Combine(removed, $"{stamp}_{targetName}"), overwrite: true);
            if (manifestFileName is not null && File.Exists(Path.Combine(target, manifestFileName)))
            {
                File.Copy(Path.Combine(target, manifestFileName), Path.Combine(removed, $"{stamp}_{manifestFileName}"), overwrite: true);
            }
        }

        AtomicFile.Copy(sourcePakPath, targetPak);

        // This app's merged pak under another name: replaced by the one just copied.
        foreach (var legacy in MergedPakNames.All.Where(n => !string.Equals(n, targetName, StringComparison.OrdinalIgnoreCase)))
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
            var targetManifest = Path.Combine(target, manifestFileName);
            if (File.Exists(manifest))
            {
                AtomicFile.Copy(manifest, targetManifest);
            }
            else if (File.Exists(targetManifest))
            {
                File.Delete(targetManifest);
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

    /// <summary>
    /// Puts UE4SS on the server from the game client's own install (same game, same engine build —
    /// verified to load on the server through dwmapi.dll): dwmapi.dll, the loader's files
    /// (Ue4ssServerFiles.LoaderFiles: no logs, no Mods), and the framework's Mods\shared + Keybinds +
    /// mods.txt. Never overwrites a settings file or mods.txt the server already has. Returns false
    /// when the game has no UE4SS to copy. A server whose UE4SS is set up the old way (UE4SS.dll,
    /// Mods\ and UE4SS-settings.ini directly in Binaries\Win64) has its mods, their on/off state and
    /// its settings moved into the ue4ss folder first (Ue4ssOldLayout), backed up under
    /// <paramref name="backupsFolder"/> and moved back if the copy fails; with no backups folder an
    /// old setup is left where it is. A loader the copy replaces with another build is kept under
    /// the backups folder first (KeepServerLoader).
    /// </summary>
    public static bool InstallUe4ssFromGame(string gameWin64Folder, string installFolder, string? backupsFolder = null) =>
        InstallUe4ssFromGame(gameWin64Folder, installFolder, backupsFolder, out _, out _);

    /// <summary>
    /// The same, also saying what an old setup's move did (what moved, what stayed in Binaries\Win64,
    /// where the backup is) and where the server's previous loader was kept (null when it had none,
    /// already had the game's build, or there is no backups folder).
    /// </summary>
    public static bool InstallUe4ssFromGame(string gameWin64Folder, string installFolder, string? backupsFolder, out Ue4ssOldLayout.Outcome movedOldSetup, out string? keptLoader)
    {
        movedOldSetup = Ue4ssOldLayout.Outcome.None;
        keptLoader = null;
        var gameUe4ss = Path.Combine(gameWin64Folder, "ue4ss");
        if (!File.Exists(Path.Combine(gameWin64Folder, "dwmapi.dll")) || !File.Exists(Path.Combine(gameUe4ss, "UE4SS.dll")))
        {
            return false;
        }

        var serverWin64 = DedicatedServerLayout.Win64Folder(installFolder);
        if (backupsFolder is null)
        {
            CopyUe4ssFromGame(gameWin64Folder, gameUe4ss, serverWin64);
            return true;
        }

        var oldLayout = Ue4ssOldLayout.MoveIntoUe4ssFolder(serverWin64, Path.Combine(backupsFolder, "UE4SS-Loader"));
        try
        {
            keptLoader = KeepServerLoader(gameWin64Folder, gameUe4ss, serverWin64, Path.Combine(backupsFolder, "UE4SS-Loader"));
            CopyUe4ssFromGame(gameWin64Folder, gameUe4ss, serverWin64);
        }
        catch (Exception)
        {
            oldLayout.Undo();
            throw;
        }

        movedOldSetup = oldLayout.ToOutcome();
        return true;
    }

    /// <summary>
    /// Before the game's UE4SS is written over the server's: copies the server's own loader (dwmapi.dll,
    /// every loader file about to be replaced, and the framework's Mods\shared and Mods\Keybinds) to
    /// loader_&lt;time&gt; under <paramref name="loaderBackups"/>, so the build the server ran can be
    /// put back. Returns that folder; null (nothing kept) when the server has no loader yet or already
    /// has the game's build.
    /// </summary>
    private static string? KeepServerLoader(string gameWin64Folder, string gameUe4ss, string serverWin64, string loaderBackups)
    {
        var serverUe4ss = Path.Combine(serverWin64, "ue4ss");
        var replaced = new List<(string Game, string Server, string Relative)>
        {
            (Path.Combine(gameWin64Folder, "dwmapi.dll"), Path.Combine(serverWin64, "dwmapi.dll"), "dwmapi.dll"),
        };
        replaced.AddRange(Ue4ssServerFiles.LoaderFiles(gameUe4ss)
            .Where(r => !string.Equals(r, Ue4ssServerFiles.SettingsFileName, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Path.Combine(gameUe4ss, r), Path.Combine(serverUe4ss, r), Path.Combine("ue4ss", r))));
        replaced.RemoveAll(f => !File.Exists(f.Server));

        // Starlink's own build note is rewritten on every copy, so it never counts as a change.
        var changes = replaced.Any(f =>
            !string.Equals(Path.GetFileName(f.Server), Ue4ssInstalledBuild.MarkerFileName, StringComparison.OrdinalIgnoreCase)
            && !SameBytes(f.Game, f.Server));
        if (!changes)
        {
            return null;
        }

        var keep = Path.Combine(loaderBackups, $"loader_{DateTime.Now:yyyyMMdd-HHmmss}");
        foreach (var (_, server, relative) in replaced)
        {
            var target = Path.Combine(keep, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(server, target, overwrite: true);
        }

        foreach (var framework in new[] { "shared", "Keybinds" })
        {
            var source = Path.Combine(serverUe4ss, "Mods", framework);
            if (Directory.Exists(source))
            {
                Directories.Copy(source, Path.Combine(keep, "ue4ss", "Mods", framework));
            }
        }

        return keep;
    }

    private static bool SameBytes(string first, string second)
    {
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        if (a.Length != b.Length)
        {
            return false;
        }

        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        int read;
        while ((read = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false)) > 0)
        {
            if (b.ReadAtLeast(bufferB, read, throwOnEndOfStream: false) != read || !bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
            {
                return false;
            }
        }

        return true;
    }

    private static void CopyUe4ssFromGame(string gameWin64Folder, string gameUe4ss, string serverWin64)
    {
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
