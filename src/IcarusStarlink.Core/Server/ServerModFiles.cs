namespace IcarusStarlink.Core.Server;

public sealed record ServerPakFile(string FileName, long SizeBytes, DateTime ModifiedLocal);

public sealed record ServerUe4ssMod(string Name, bool Enabled);

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

    /// <summary>Copies a merged pak (and its sibling ISL-Merged.txt manifest, when present) into the server's mods folder, replacing an older copy of the same file.</summary>
    /// <summary>The merged pak's earlier file name(s) (see PakIO's InstallManifestNames; Core can't reference PakIO).</summary>
    public static IReadOnlyList<string> LegacyMergedPakFileNames { get; } = ["ISL-Merged_P.pak", "ISL-Merged_9999_P.pak"];

    public static void InstallPak(string sourcePakPath, string installFolder, string? manifestFileName)
    {
        var target = DedicatedServerLayout.PakModsFolder(installFolder);
        Directory.CreateDirectory(target);
        File.Copy(sourcePakPath, Path.Combine(target, Path.GetFileName(sourcePakPath)), overwrite: true);

        // This app's merged pak under its earlier name: replaced by the one just copied.
        foreach (var legacy in LegacyMergedPakFileNames.Where(n => !string.Equals(n, Path.GetFileName(sourcePakPath), StringComparison.OrdinalIgnoreCase)))
        {
            var old = Path.Combine(target, legacy);
            if (File.Exists(old))
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

    /// <summary>UE4SS mod folders on the server with their mods.txt state (an enabled.txt inside a folder also counts as on).</summary>
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
            .Select(n => new ServerUe4ssMod(n!, txt.IsEnabled(n!) ?? File.Exists(Path.Combine(mods, n!, "enabled.txt"))))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Puts UE4SS on the server from the game client's own install (same game, same engine build —
    /// verified to load on the server through dwmapi.dll): dwmapi.dll, UE4SS.dll, UE4SS-settings.ini,
    /// and the framework's Mods\shared + Keybinds + mods.txt. Never overwrites a settings file or
    /// mods.txt the server already has. Returns false when the game has no UE4SS to copy.
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
        File.Copy(Path.Combine(gameUe4ss, "UE4SS.dll"), Path.Combine(serverUe4ss, "UE4SS.dll"), overwrite: true);
        CopyIfMissing(Path.Combine(gameUe4ss, "UE4SS-settings.ini"), Path.Combine(serverUe4ss, "UE4SS-settings.ini"));
        CopyIfMissing(Path.Combine(gameUe4ss, "Mods", "mods.txt"), Path.Combine(serverUe4ss, "Mods", "mods.txt"));
        foreach (var framework in new[] { "shared", "Keybinds" })
        {
            var source = Path.Combine(gameUe4ss, "Mods", framework);
            if (Directory.Exists(source))
            {
                CopyDirectory(source, Path.Combine(serverUe4ss, "Mods", framework));
            }
        }

        return true;
    }

    /// <summary>Copies one UE4SS mod folder from the game to the server (replacing the server's copy) and turns it on in the server's mods.txt.</summary>
    public static void CopyUe4ssModToServer(string gameModsFolder, string modName, string installFolder)
    {
        if (modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{modName}' isn't a folder name.", nameof(modName));
        }

        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(installFolder);
        var target = Path.Combine(serverMods, modName);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        CopyDirectory(Path.Combine(gameModsFolder, modName), target);
        var txtPath = Path.Combine(serverMods, "mods.txt");
        var txt = Ue4ssModsTxt.Load(txtPath);
        txt.Set(modName, true);
        txt.Save(txtPath);
    }

    /// <summary>Moves a UE4SS mod folder off the server into the backups folder and drops its mods.txt line.</summary>
    public static void RemoveUe4ssMod(string installFolder, string modName, string backupsFolder)
    {
        if (modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{modName}' isn't a folder name.", nameof(modName));
        }

        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(installFolder);
        var source = Path.Combine(serverMods, modName);
        if (Directory.Exists(source))
        {
            var dest = Path.Combine(backupsFolder, "removed-ue4ss", $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{modName}");
            CopyDirectory(source, dest);
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

    private static void CopyIfMissing(string source, string target)
    {
        if (File.Exists(source) && !File.Exists(target))
        {
            File.Copy(source, target);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
