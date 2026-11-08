using System.IO.Compression;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// Installs or updates the UE4SS loader itself (dwmapi.dll and Binaries\Win64\ue4ss\UE4SS.dll). It writes
/// into the game's own program folder, so it is careful: the whole archive is checked (layout, every path,
/// every size) before anything is touched; the ue4ss\ folder and dwmapi.dll are backed up first; every file
/// is written beside its place and moved in, dwmapi.dll last; and if one can't be written, the files already
/// written are put back from that backup. Mods\ (the player's mods, the built-in ones, and mods.txt /
/// mods.json, which say what is on) and UE4SS-settings.ini are only ever added to, never overwritten, except
/// Mods\shared\, UE4SS's own Lua library, which the release replaces. Everything else (UE4SS.dll, dwmapi.dll,
/// README/Changelog/LICENSE, and UE4SS-settings.default.ini, the release's own settings file kept beside the
/// player's) always gets the new copy: that is the update.
/// </summary>
public sealed class Ue4ssLoaderInstallService : IUe4ssLoaderInstallService
{
    public Ue4ssLoaderStatus GetStatus(string icarusContentPath)
    {
        var dllPath = Ue4ssGamePaths.ResolveLoaderDllPath(icarusContentPath);
        var dwmapiPath = Ue4ssGamePaths.ResolveDwmapiPath(icarusContentPath);
        if (!File.Exists(dllPath) || !File.Exists(dwmapiPath))
        {
            return new Ue4ssLoaderStatus(IsInstalled: false, InstalledVersion: null);
        }

        var (version, commit) = Ue4ssInstalledBuild.Read(Ue4ssGamePaths.ResolveLoaderFolder(icarusContentPath));
        return new Ue4ssLoaderStatus(IsInstalled: true, version, commit);
    }

    public IReadOnlyList<string> ListNativeMods(string icarusContentPath)
    {
        var modsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);
        if (!Directory.Exists(modsFolder))
        {
            return [];
        }

        return [.. Directory.GetDirectories(modsFolder)
            .Where(d => File.Exists(Path.Combine(d, "dlls", "main.dll")))
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    public Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, CancellationToken cancellationToken = default) =>
        InstallOrUpdateAsync(icarusContentPath, downloadedZipPath, backupDirectory, knownVersion: null, knownCommit: null, cancellationToken);

    public Task InstallOrUpdateAsync(
        string icarusContentPath, string downloadedZipPath, string backupDirectory, string? knownVersion, string? knownCommit,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            InstallOrUpdateCore(icarusContentPath, downloadedZipPath, backupDirectory);
            Ue4ssInstalledBuild.RecordInstalled(Ue4ssGamePaths.ResolveLoaderFolder(icarusContentPath), knownVersion, knownCommit);
        }, cancellationToken);

    private static void InstallOrUpdateCore(string icarusContentPath, string downloadedZipPath, string backupDirectory)
    {
        var win64Folder = Ue4ssGamePaths.ResolveWin64Folder(icarusContentPath);
        var loaderFolder = Ue4ssGamePaths.ResolveLoaderFolder(icarusContentPath);
        var dwmapiPath = Ue4ssGamePaths.ResolveDwmapiPath(icarusContentPath);
        var ue4ssBackupDirectory = Path.Combine(backupDirectory, "UE4SS-Loader");

        // Validated BEFORE anything is backed up or written: PlanWrites keys
        // its dwmapi.dll placement and its additive-only protection for Mods and UE4SS-settings.ini
        // on the entry's first path segment, so a release zip with an unexpected layout (a single
        // wrapping folder — the shape newer UE4SS builds have shipped — or no UE4SS.dll at all)
        // used to go through the full backup and then land a broken, misplaced install with no
        // error. A single wrapping folder is stripped (same convention UE4SS mod imports use);
        // anything else without the two loader files at its root is refused untouched.
        // Two real release layouts:
        // - flat (official v3.0.1): dwmapi.dll, UE4SS.dll, UE4SS-settings.ini and Mods\ all at the
        //   root; everything but dwmapi.dll goes into Binaries\Win64\ue4ss.
        // - game-layout (every dev build since): dwmapi.dll at the root plus a ue4ss\ folder that
        //   mirrors Binaries\Win64\ue4ss exactly.
        using var archive = ZipFile.OpenRead(downloadedZipPath);
        var wrappingPrefix = FindSingleWrappingFolderPrefix(archive);
        var paths = archive.Entries
            .Select(e => StripWrapper(e.FullName, wrappingPrefix))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gameLayout = paths.Contains("dwmapi.dll") && paths.Contains("ue4ss/UE4SS.dll");
        if (!gameLayout && (!paths.Contains("UE4SS.dll") || !paths.Contains("dwmapi.dll")))
        {
            throw new InvalidOperationException(
                "This UE4SS archive doesn't have the expected layout (dwmapi.dll with UE4SS.dll next to it or in a ue4ss folder, optionally inside one wrapping folder) — nothing was changed. It may be a different kind of UE4SS download; get the release zip itself.");
        }

        // Pass 1: every write is planned and checked (path, size) before anything is backed up or
        // touched. A bad entry near the end used to throw after dwmapi.dll (the zip's first file)
        // was already replaced, leaving a new dwmapi.dll beside an old or missing UE4SS.dll.
        List<PlannedWrite> plan;
        try
        {
            plan = PlanWrites(archive, wrappingPrefix, gameLayout, win64Folder, loaderFolder);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"This UE4SS archive can't be installed: {ex.Message} Nothing was changed.", ex);
        }

        var hadLoaderFolder = Directory.Exists(loaderFolder);
        var earlierBackups = FolderBackup.EnumerateFolderBackups(ue4ssBackupDirectory, "ue4ss").ToHashSet(StringComparer.OrdinalIgnoreCase);
        FolderBackup.BackupFolder(loaderFolder, ue4ssBackupDirectory);
        var loaderBackup = FolderBackup.EnumerateFolderBackups(ue4ssBackupDirectory, "ue4ss").FirstOrDefault(b => !earlierBackups.Contains(b));
        var dwmapiBackup = FolderBackup.BackupFile(dwmapiPath, ue4ssBackupDirectory);

        Directory.CreateDirectory(win64Folder);
        Directory.CreateDirectory(loaderFolder);

        // Pass 2: each file is written beside its place and moved in (a failed write leaves the old
        // file whole), dwmapi.dll last. If one can't be written, the ones already written are put back.
        var written = new List<PlannedWrite>();
        try
        {
            foreach (var item in plan)
            {
                var entry = item.Entry;
                Directory.CreateDirectory(Path.GetDirectoryName(item.Dest)!);
                AtomicFile.Write(item.Dest, temp =>
                {
                    using var entryStream = entry.Open();
                    using var fileStream = File.Create(temp);
                    BoundedZipEntryCopy.CopyBounded(entryStream, fileStream, entry.Length, entry.FullName);
                });
                written.Add(item);
            }
        }
        catch (Exception ex)
        {
            var notPutBack = PutBack(written, loaderFolder, loaderBackup, dwmapiBackup, hadLoaderFolder);
            throw new IOException(
                notPutBack.Count == 0
                    ? $"{ex.Message} Nothing was changed: the files already written were put back."
                    : $"{ex.Message} Starlink couldn't put back {string.Join(", ", notPutBack)}. Your previous UE4SS is in {ue4ssBackupDirectory}.",
                ex);
        }
    }

    /// <param name="ExistedBefore">The file was there before the install: a failed install puts the backup copy back instead of deleting it.</param>
    /// <param name="IsDwmapi">The top-level dwmapi.dll, backed up on its own beside the ue4ss folder's backup.</param>
    private sealed record PlannedWrite(ZipArchiveEntry Entry, string Dest, bool ExistedBefore, bool IsDwmapi);

    /// <summary>
    /// The files the archive puts in the game, in the order they're written (dwmapi.dll last: it is
    /// what makes the game load UE4SS, so a first install that stops partway leaves the game without
    /// it, and an update replaces it only after everything else went in). Throws FormatException for
    /// a path outside the game's folders or an entry over the size budget.
    /// </summary>
    private static List<PlannedWrite> PlanWrites(ZipArchive archive, string? wrappingPrefix, bool gameLayout, string win64Folder, string loaderFolder)
    {
        var sizeBudget = new ExmodSizeBudget("UE4SS release archive");
        var plan = new List<PlannedWrite>();
        var additiveDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue; // a directory entry, nothing to write
            }

            var relativePath = StripWrapper(entry.FullName, wrappingPrefix)!;
            var isTopLevelDwmapi = relativePath.Equals("dwmapi.dll", StringComparison.OrdinalIgnoreCase);
            if (gameLayout && !isTopLevelDwmapi)
            {
                // Only the ue4ss\ folder and dwmapi.dll belong in the game; anything else at the
                // zip's root (a readme) is left out.
                if (!relativePath.StartsWith("ue4ss/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relativePath = relativePath["ue4ss/".Length..];
            }

            var segments = relativePath.Split('/');
            var isUnderMods = segments[0].Equals("Mods", StringComparison.OrdinalIgnoreCase);
            var isUnderShared = isUnderMods && segments.Length > 2 && segments[1].Equals("shared", StringComparison.OrdinalIgnoreCase);
            var isSettingsIni = segments.Length == 1 && segments[0].Equals("UE4SS-settings.ini", StringComparison.OrdinalIgnoreCase);

            var destPath = isTopLevelDwmapi
                ? AssetPathGuard.ResolveWithinDirectory(win64Folder, "dwmapi.dll")
                : AssetPathGuard.ResolveWithinDirectory(loaderFolder, relativePath);

            // The release's own settings file always goes beside the player's as
            // UE4SS-settings.default.ini (UE4SS never reads it): the player's file below is kept, so
            // this is where the UE4SS config tab finds the settings a newer build added.
            if (isSettingsIni)
            {
                var defaultPath = AssetPathGuard.ResolveWithinDirectory(loaderFolder, Ue4ssServerFiles.DefaultSettingsFileName);
                sizeBudget.Charge($"UE4SS release entry '{entry.FullName}'", entry.Length);
                plan.Add(new PlannedWrite(entry, defaultPath, File.Exists(defaultPath), IsDwmapi: false));
            }

            // Mods\ (the player's mods, the built-in ones, and mods.txt / mods.json, which say what
            // is on) and UE4SS-settings.ini (the player's own settings) are only ever added to,
            // never overwritten. Mods\shared\ is UE4SS's own Lua library (UEHelpers and its type
            // files), not a mod: the release's copy replaces the old one, or mods written for the
            // new build's helpers fail to load. The player's own files there stay; only the
            // release's files are written. Everything else (UE4SS.dll, dwmapi.dll,
            // README/Changelog/LICENSE) always gets the new copy: that is the update.
            var additiveOnly = (isUnderMods && !isUnderShared) || isSettingsIni;
            if (additiveOnly && (File.Exists(destPath) || Directory.Exists(destPath) || !additiveDestinations.Add(destPath)))
            {
                continue;
            }

            sizeBudget.Charge($"UE4SS release entry '{entry.FullName}'", entry.Length);
            plan.Add(new PlannedWrite(entry, destPath, File.Exists(destPath), isTopLevelDwmapi));
        }

        // A stable sort: everything in the archive's order, then dwmapi.dll.
        return [.. plan.OrderBy(p => p.IsDwmapi)];
    }

    /// <summary>
    /// Undoes a failed install's writes, newest first: a file that wasn't there before is deleted,
    /// one that was is copied back from the backup made just before. A file whose backup copy is
    /// missing is left as it is, never deleted. Returns the names that couldn't be put back.
    /// </summary>
    private static List<string> PutBack(
        List<PlannedWrite> written, string loaderFolder, string? loaderBackup, string? dwmapiBackup, bool hadLoaderFolder)
    {
        var notPutBack = new List<string>();
        foreach (var item in Enumerable.Reverse(written))
        {
            try
            {
                if (!item.ExistedBefore)
                {
                    File.Delete(item.Dest);
                    continue;
                }

                var backupCopy = item.IsDwmapi
                    ? dwmapiBackup
                    : loaderBackup is null ? null : Path.Combine(loaderBackup, Path.GetRelativePath(loaderFolder, item.Dest));
                if (backupCopy is null || !File.Exists(backupCopy))
                {
                    notPutBack.Add(Path.GetFileName(item.Dest));
                    continue;
                }

                AtomicFile.Copy(backupCopy, item.Dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notPutBack.Add(Path.GetFileName(item.Dest));
            }
        }

        // A first install: the loader folder holds only folders this install made.
        if (!hadLoaderFolder && notPutBack.Count == 0)
        {
            Directories.TryDelete(loaderFolder);
        }

        return notPutBack;
    }

    /// <summary>The single top-level folder every file entry sits under, as "Name/" — or null when files sit at the root (the classic flat layout) or the top level is mixed.</summary>
    private static string? FindSingleWrappingFolderPrefix(ZipArchive archive)
    {
        string? candidate = null;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.EndsWith('/'))
            {
                continue;
            }

            var slash = normalized.IndexOf('/');
            if (slash <= 0)
            {
                return null; // a file at the very root — nothing wraps it
            }

            var top = normalized[..(slash + 1)];
            if (candidate is null)
            {
                candidate = top;
            }
            else if (!string.Equals(candidate, top, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return candidate;
    }

    /// <summary>Normalizes separators and drops the wrapping folder; null for a directory entry.</summary>
    private static string? StripWrapper(string entryFullName, string? wrappingPrefix)
    {
        var normalized = entryFullName.Replace('\\', '/');
        if (normalized.EndsWith('/'))
        {
            return null;
        }

        return wrappingPrefix is not null && normalized.StartsWith(wrappingPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[wrappingPrefix.Length..]
            : normalized;
    }

    public IReadOnlyList<string> ListUserAddedMods(string icarusContentPath)
    {
        var modsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);
        if (!Directory.Exists(modsFolder))
        {
            return [];
        }

        var frameworkOwned = Ue4ssFrameworkMods.In(modsFolder);
        return [.. Directory.GetDirectories(modsFolder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !frameworkOwned.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// UE4SS's own mods are the known built-ins plus the installed mods.json (Ue4ssFrameworkMods.In).
    /// A mod it can't place counts as the player's: Uninstall keeps it in staging, never the reverse.
    /// </summary>
    public bool IsFrameworkOwned(string icarusContentPath, string modName) =>
        Ue4ssFrameworkMods.In(Ue4ssGamePaths.ResolveModsFolder(icarusContentPath)).Contains(modName);

    public Task<Ue4ssUninstallResult> UninstallAsync(
        string icarusContentPath, string stagedModsDirectory, string backupDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var loaderFolder = Ue4ssGamePaths.ResolveLoaderFolder(icarusContentPath);
            var dwmapiPath = Ue4ssGamePaths.ResolveDwmapiPath(icarusContentPath);
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);

            // Ordering is the safety argument, so it's spelled out:
            // 1. PRESERVE — every user-added mod is copied into this app's staging first, so even
            //    a failure right after this point has lost nothing of the user's.
            // 2. BACKUP the whole ue4ss folder + dwmapi.dll (keep-last-5, the installer's own
            //    rotation) — the full-undo path, framework mods and all.
            // 3. DELETE — only after both of the above have succeeded, dwmapi.dll first.
            var preservedMods = new List<string>();
            foreach (var modName in ListUserAddedMods(icarusContentPath))
            {
                var target = UniqueDirectoryName(stagedModsDirectory, modName);
                FolderBackup.CopyDirectory(Path.Combine(modsFolder, modName), Path.Combine(stagedModsDirectory, target));
                preservedMods.Add(target);
            }

            var ue4ssBackupDirectory = Path.Combine(backupDirectory, "UE4SS-Loader");
            FolderBackup.BackupFolder(loaderFolder, ue4ssBackupDirectory);
            FolderBackup.BackupFile(dwmapiPath, ue4ssBackupDirectory);

            // dwmapi.dll first: it's what makes the game load UE4SS, so if the folder delete below
            // fails partway (a file locked while the game runs) the game is left clean, not
            // loading a half-deleted UE4SS.
            if (File.Exists(dwmapiPath))
            {
                File.Delete(dwmapiPath);
            }

            if (Directory.Exists(loaderFolder))
            {
                Directory.Delete(loaderFolder, recursive: true);
            }

            return new Ue4ssUninstallResult(preservedMods, ue4ssBackupDirectory);
        }, cancellationToken);

    private static string UniqueDirectoryName(string rootDirectory, string desiredName)
    {
        var candidate = desiredName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(rootDirectory, candidate)))
        {
            candidate = $"{desiredName}_{++suffix}";
        }

        return candidate;
    }
}
