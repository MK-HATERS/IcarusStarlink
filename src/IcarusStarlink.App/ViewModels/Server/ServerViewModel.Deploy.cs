using System.IO;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The three deploy flows: the merged pak, the UE4SS loader and the UE4SS mods, each onto the connected server.</summary>
public sealed partial class ServerViewModel
{
    /// <summary>The temporary name a file is uploaded under before it replaces the old one ("ISL-Merged_9999_P.pak.uploading" — not a .pak, so the game never loads a half-uploaded file).</summary>
    internal static string UploadingName(string fileName) => fileName + ".uploading";

    private static readonly string[] PakFamilyExtensions = [".pak", ".sig", ".utoc", ".ucas"];

    /// <summary>What Install merged pak clears out of the server's mods folder: pak files, the merge's own list, and our own stale uploads. Anything else there is left alone.</summary>
    private bool IsClearedFromModsFolder(string name) =>
        PakFamilyExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        || string.Equals(name, Path.GetFileName(_pakManifestPath), StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(UploadingName(""), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Uploads the app's own last-built merged pak (Merge &amp; Install's own Staged_Build output —
    /// the same file "Install" copies into the LOCAL game folder) to the server's own mods folder,
    /// replacing whatever's there. The server's mods folder is meant to hold exactly one active
    /// merged pak, so this clears out its old pak files (only pak files, and nothing at all when the
    /// path doesn't end in a "mods" folder) — but always names every file it's about to delete
    /// (and upload) in the confirmation prompt, rather than a bare "are you sure?".
    ///
    /// A site already known (from a past attempt) not to support delete skips trying it at all and
    /// instead warns UP FRONT, before ever touching the server, that old files will remain, rather
    /// than silently uploading first and reporting the leftover files only afterward, since a dedicated server actively serving players is higher-stakes
    /// than the local game folder if it ends up loading two conflicting merged paks at once. A site
    /// not yet known either way still tries each delete for real (learning the answer for next
    /// time), but never lets one rejected delete abort the whole upload.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task InstallPakToServerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_connectedClient is null)
        {
            ConnectionStatusMessage = "Connect to a site first.";
            return;
        }

        if (!File.Exists(_outputPakPath))
        {
            ConnectionStatusMessage = "No merged pak yet — build one on the Merge & Install page first.";
            return;
        }

        IsBusy = true;
        try
        {
            // The remote UE4SS folder can't be read here, but ArcFission Library writes its data pak
            // into this mods folder: one there means the library runs on that server, so the merge
            // goes up as zz_zz_…_9999 to outrank it; otherwise under the plain name.
            var remoteFiles = await _connectedClient.ListDirectoryAsync(RemoteModsPath);
            var pakName = IcarusStarlink.Core.Library.MergedPakNames.For(
                remoteFiles.Any(e => !e.IsDirectory && IcarusStarlink.PakIO.InstallManifestNames.IsArcFissionDataPak(e.Name)));
            var toUpload = new List<string> { pakName };
            if (File.Exists(_pakManifestPath))
            {
                toUpload.Add(Path.GetFileName(_pakManifestPath));
            }

            // The game loads mods only from a folder named "mods". A Mods path one level too high
            // (Icarus/Content/Paks) used to put the game's own base paks up for deletion, so outside
            // a "mods" folder nothing is deleted: only the new pak goes up.
            var inModsFolder = string.Equals(RemoteModsPath.TrimEnd('/').Split('/')[^1], "mods", StringComparison.OrdinalIgnoreCase);

            // ArcFission Library's own data pak stays: the server's library writes it again anyway.
            // A leftover upload from an earlier attempt that broke off is replaced, not listed.
            // Only pak files, the merge's list and our own stale uploads are cleared out; anything
            // else (a readme, a host's own file) is left alone.
            var candidates = remoteFiles
                .Where(e => !e.IsDirectory && !IcarusStarlink.PakIO.InstallManifestNames.IsArcFissionDataPak(e.Name))
                .Where(e => !toUpload.Any(name => string.Equals(e.Name, UploadingName(name), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var existing = inModsFolder ? candidates.Where(e => IsClearedFromModsFolder(e.Name)).ToList() : [];
            var leftAlone = candidates
                .Where(e => !existing.Contains(e) && !toUpload.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var leftAloneNote = leftAlone.Count > 0
                ? "\n\nLeft alone:\n" + string.Join('\n', leftAlone.Select(e => $"  - {e.Name}"))
                : "";
            var notModsFolderNote = inModsFolder
                ? ""
                : "This path doesn't end in a folder named 'mods', so it may not be the server's mods folder. Nothing there is deleted; only the new pak goes up.\n\n";
            const string stopServerNote = "\n\nStop the server on the host first: its pak files are locked while it runs.";

            // _connectedSite, not SelectedSite: this gates a real delete attempt against the LIVE
            // connection, and must reflect what's actually known about the site being acted on, not
            // whatever row happens to be selected in the list right now (see _connectedSite's own
            // doc comment on why those two can drift).
            var knownBlocked = existing.Count > 0 && _connectedSite?.SupportsDelete == false;

            // A file with the same name as one going up is written over, not left behind: on a host
            // that blocks delete, the old merged pak of the same name used to be listed as "will
            // remain" and reported afterwards as a leftover that could load twice.
            bool IsReplaced(string name) => toUpload.Contains(name, StringComparer.OrdinalIgnoreCase);
            var leftBehind = existing.Where(e => !IsReplaced(e.Name)).ToList();
            string prompt;
            if (knownBlocked && leftBehind.Count > 0)
            {
                prompt =
                    $"'{_connectedSite?.Name}' doesn't allow deleting files via FTP, so the existing file(s) below will remain after this upload:\n"
                    + string.Join('\n', leftBehind.Select(e => $"  - {e.Name}"))
                    + $"\n\nUpload will add:\n{string.Join('\n', toUpload.Select(n => $"  - {n}"))}{leftAloneNote}\n\n"
                    + $"You'll need to remove the old file(s) yourself via {_connectedSite?.Name}'s own file manager afterward — otherwise the server may end up loading multiple conflicting merged paks.{stopServerNote}\n\nUpload anyway?";
            }
            else if (knownBlocked)
            {
                prompt =
                    $"This will upload:\n{string.Join('\n', toUpload.Select(n => $"  - {n}"))}\n\n"
                    + $"to '{RemoteModsPath}' on '{_connectedSite?.Name}', writing over the copies already there.{leftAloneNote}{stopServerNote}\n\nContinue?";
            }
            else
            {
                var deleteList = existing.Count > 0
                    ? "\n\nOnce it is fully uploaded, these are deleted:\n" + string.Join('\n', existing.Select(e => $"  - {e.Name}"))
                    : "";
                prompt =
                    $"{notModsFolderNote}This will upload:\n{string.Join('\n', toUpload.Select(n => $"  - {n}"))}\n\n"
                    + $"to '{RemoteModsPath}' on '{_connectedSite?.Name}'.{deleteList}{leftAloneNote}{stopServerNote}\n\nContinue?";
            }

            if (!(_dialogService.Confirm(prompt, "Install merged pak to server", ThemedConfirmSeverity.Warning)))
            {
                return;
            }

            var localFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [pakName] = _outputPakPath,
            };
            if (File.Exists(_pakManifestPath))
            {
                localFor[Path.GetFileName(_pakManifestPath)] = _pakManifestPath;
            }

            var deleteFailures = new List<string>();
            if (knownBlocked)
            {
                // Nothing can be deleted there: upload straight over the names.
                deleteFailures.AddRange(leftBehind.Select(e => e.Name));
                foreach (var name in toUpload)
                {
                    await _connectedClient.UploadFileAsync(localFor[name], $"{RemoteModsPath}/{name}");
                }
            }
            else
            {
                // Upload under a temporary name first (the game only loads *.pak), and only once
                // that has fully arrived delete the old files and rename the new ones into place: a
                // dropped connection mid-upload used to leave the server with its old paks already
                // deleted and the new one half there.
                foreach (var name in toUpload)
                {
                    await _connectedClient.UploadFileAsync(localFor[name], $"{RemoteModsPath}/{UploadingName(name)}");
                }

                foreach (var entry in existing)
                {
                    try
                    {
                        await _connectedClient.DeleteFileAsync($"{RemoteModsPath}/{entry.Name}");
                    }
                    catch (FtpOperationRejectedException)
                    {
                        deleteFailures.Add(entry.Name);
                    }
                }

                var deletesWorked = deleteFailures.Count == 0;

                foreach (var name in toUpload)
                {
                    try
                    {
                        await _connectedClient.RenameFileAsync($"{RemoteModsPath}/{UploadingName(name)}", $"{RemoteModsPath}/{name}");
                    }
                    catch (FtpOperationRejectedException)
                    {
                        // The server won't rename: upload it again under its real name, then tidy the temporary copy.
                        await _connectedClient.UploadFileAsync(localFor[name], $"{RemoteModsPath}/{name}");
                        try
                        {
                            await _connectedClient.DeleteFileAsync($"{RemoteModsPath}/{UploadingName(name)}");
                        }
                        catch (FtpOperationRejectedException)
                        {
                            deleteFailures.Add(UploadingName(name));
                        }
                    }
                }

                // Recorded only once the new files are in place: nothing sits between the deletes
                // and the renames that could stop the install half done.
                if (existing.Count > 0)
                {
                    RememberDeleteCapability(deletesWorked);
                }

                // A same-name file whose delete was refused has been written over by now.
                deleteFailures.RemoveAll(IsReplaced);
            }

            if (deleteFailures.Count > 0)
            {
                ConnectionStatusMessage =
                    $"Installed the new pak, but couldn't remove {deleteFailures.Count} old file(s) on '{_connectedSite?.Name}' — "
                    + $"remove them yourself via its file manager, or the server may load multiple conflicting merged paks.";
                _activityLog.Log(
                    $"Installed the merged pak to FTP site '{_connectedSite?.Name}', but {deleteFailures.Count} old file(s) ({string.Join(", ", deleteFailures)}) couldn't be removed.",
                    ActivityEntryKind.Warning);
            }
            else
            {
                ConnectionStatusMessage = $"Installed the merged pak to '{_connectedSite?.Name}'.";
                _activityLog.Log($"Installed the merged pak to FTP site '{_connectedSite?.Name}' (replaced {existing.Count} file(s)).", ActivityEntryKind.Success);
            }
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Install to server failed: {ex.Message}";
            _activityLog.Log($"Install to server '{_connectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Checks the server's own UE4SS.log version marker against the locally-installed one (the
    /// same real signal Ue4ssLoaderInstallService already uses for the local check — UE4SS.dll
    /// itself carries no Win32 version resource) and, if they differ, uploads the loader — never
    /// the server's own Mods\ folder, and the game's UE4SS-settings.ini only when the server surely
    /// has none, so a loader update can't clobber whatever's already configured on the server.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task SyncUe4ssLoaderToServerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_connectedClient is null)
        {
            ConnectionStatusMessage = "Connect to a site first.";
            return;
        }

        if (_settingsService.Current.IcarusContentPath is not { } contentPath)
        {
            ConnectionStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        var localLoaderFolder = Ue4ssGamePaths.ResolveLoaderFolder(contentPath);
        if (!File.Exists(Ue4ssGamePaths.ResolveLoaderDllPath(contentPath)))
        {
            ConnectionStatusMessage = "UE4SS isn't installed locally — nothing to sync.";
            return;
        }

        // Every UE4SS build since 3.0.1 says "v3.0.1": the commit is what tells two builds apart.
        var (localVersion, localCommit) = Ue4ssInstalledBuild.Read(localLoaderFolder);
        var localLabel = Ue4ssBuild.Describe(localVersion, localCommit);

        IsBusy = true;
        string? tempLogPath = null;
        var replaced = new List<string>();
        try
        {
            // Inside the try: a %TEMP% that can't take another temp file used to leave the page busy for good.
            tempLogPath = Path.GetTempFileName();
            string? remoteVersion = null;
            string? remoteCommit = null;
            try
            {
                await _connectedClient.DownloadFileAsync($"{RemoteLoaderPath}/UE4SS.log", tempLogPath);
                var remoteLines = File.ReadLines(tempLogPath).Take(20).ToList();
                remoteVersion = Ue4ssLogVersionParser.Parse(remoteLines);
                remoteCommit = Ue4ssLogVersionParser.ParseCommit(remoteLines);
            }
            catch (Exception)
            {
                // No UE4SS.log on the server yet (or unreadable) — treated as "not installed",
                // same as the local Ue4ssLoaderInstallService.GetStatus contract.
            }

            var sameBuild = remoteVersion is not null
                && string.Equals(remoteVersion, localVersion, StringComparison.OrdinalIgnoreCase)
                && (Ue4ssBuild.SameCommit(remoteCommit, localCommit) || (remoteCommit is null && localCommit is null));
            if (sameBuild)
            {
                ConnectionStatusMessage = $"Server already has UE4SS {localLabel} — nothing to do.";
                return;
            }

            // Your UE4SS-settings.ini goes up only when the server surely has none: a listing that
            // failed (a slow host timing out) used to read as "none", and yours went over the server's.
            var remoteHasSettings = await ServerHasUe4ssSettingsAsync();
            var couldntCheckNote = remoteHasSettings is null
                ? " Couldn't check whether the server has its own UE4SS-settings.ini, so yours isn't sent (the server's, if any, is kept)."
                : "";

            // The same loader files a local server gets: no Mods folder, and none of the game's own
            // logs or crash dumps (those used to be uploaded over the server's own UE4SS.log).
            var localFiles = Ue4ssServerFiles.LoaderFiles(localLoaderFolder)
                .Where(r => remoteHasSettings == false || !string.Equals(r, Ue4ssServerFiles.SettingsFileName, StringComparison.OrdinalIgnoreCase))
                .Select(r => Path.Combine(localLoaderFolder, r))
                .ToList();

            var prompt =
                $"This will upload {localFiles.Count} loader file(s) (UE4SS.dll, dwmapi.dll, etc.) to '{RemoteWin64Path}' on "
                + $"'{_connectedSite?.Name}', replacing its current loader ({(remoteVersion is null ? "not installed" : Ue4ssBuild.Describe(remoteVersion, remoteCommit))}) with yours ({localLabel}). "
                + $"The server's own Mods folder{(remoteHasSettings == true ? " and its existing UE4SS-settings.ini" : "")} won't be touched.{couldntCheckNote} Continue?";
            if (!(_dialogService.Confirm(prompt, "Sync UE4SS loader to server", ThemedConfirmSeverity.Warning)))
            {
                return;
            }

            // Each file goes up under a temporary name and replaces the live one only once it has
            // fully arrived: a dropped connection used to leave a cut-off UE4SS.dll on the server.
            // The loader first, the proxy dwmapi.dll that starts it last.
            foreach (var localFile in localFiles)
            {
                var relative = Path.GetRelativePath(localLoaderFolder, localFile).Replace('\\', '/');
                await ReplaceRemoteFileAsync(localFile, $"{RemoteLoaderPath}/{relative}");
                replaced.Add(relative);
            }

            await ReplaceRemoteFileAsync(Ue4ssGamePaths.ResolveDwmapiPath(contentPath), $"{RemoteWin64Path}/dwmapi.dll");

            ConnectionStatusMessage = $"Synced UE4SS {localLabel} to '{_connectedSite?.Name}'.{couldntCheckNote}";
            _activityLog.Log($"Synced UE4SS loader ({localLabel}) to FTP site '{_connectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            var reason = ex.Message.TrimEnd('.');
            ConnectionStatusMessage = replaced.Count == 0
                ? $"UE4SS sync failed: {reason}. Nothing on the server was changed; try again."
                : $"UE4SS sync stopped part way: {reason}. {replaced.Count} file(s) were already replaced ({string.Join(", ", replaced)}); run Sync UE4SS loader again before you start the server.";
            _activityLog.Log($"UE4SS loader sync to '{_connectedSite?.Name}': {ConnectionStatusMessage}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsBusy = false;
            DeleteTempFile(tempLogPath);
        }
    }

    /// <summary>
    /// Whether the server's ue4ss folder holds a UE4SS-settings.ini: true or false when Starlink
    /// could see, null when it couldn't. An empty or failed listing of the ue4ss folder proves
    /// nothing (some servers refuse to list a missing folder, a cut-off listing reads as empty),
    /// so then the Win64 folder above is asked: listed with files and no ue4ss folder is a fresh
    /// server with no settings file.
    /// </summary>
    private async Task<bool?> ServerHasUe4ssSettingsAsync()
    {
        try
        {
            var loader = await _connectedClient!.ListDirectoryAsync(RemoteLoaderPath);
            if (loader.Count > 0)
            {
                return loader.Any(e => !e.IsDirectory && string.Equals(e.Name, Ue4ssServerFiles.SettingsFileName, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception)
        {
        }

        try
        {
            var win64 = await _connectedClient!.ListDirectoryAsync(RemoteWin64Path);
            if (win64.Count > 0 && !win64.Any(e => e.IsDirectory && string.Equals(e.Name, "ue4ss", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    /// <summary>Deletes a temporary file, ignoring one a scanner still holds: it is in %TEMP%, and a failed delete must not leave the page busy.</summary>
    private static void DeleteTempFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Replaces a file on the server through a temporary name: the upload goes to
    /// "name.uploading" and is renamed onto the real name only once it has fully arrived, so a
    /// dropped connection leaves the server's old file whole. A server that won't rename gets the
    /// file straight over the real name, and the temporary copy is tidied when it can be.
    /// </summary>
    private async Task ReplaceRemoteFileAsync(string localPath, string remotePath)
    {
        var temp = UploadingName(remotePath);
        await _connectedClient!.UploadFileAsync(localPath, temp);
        try
        {
            await _connectedClient.RenameFileAsync(temp, remotePath);
        }
        catch (FtpOperationRejectedException)
        {
            await _connectedClient.UploadFileAsync(localPath, remotePath);
            try
            {
                await _connectedClient.DeleteFileAsync(temp);
            }
            catch (FtpOperationRejectedException)
            {
                // Not a name UE4SS or the game reads; harmless if it stays.
            }
        }
    }

    /// <summary>
    /// Uploads the UE4SS mods that are on in the game (Ue4ssServerFiles.ModsOnInGame: the same rule
    /// a local server uses, so switched-off mods and the framework's own stay home) and aren't on
    /// the server yet, then turns them on in the server's mods.txt (it used to upload
    /// every installed mod and switch none of them on there). A mod on in the game whose folder is
    /// already on the server but that nothing there turns on (no mods.txt line, no enabled.txt:
    /// an earlier sync whose mods.txt step failed) is turned on too, without uploading it again.
    /// Nothing is removed or written over: a mod's files on the server, and an existing line such
    /// as "Name : 0", stay as the server has them.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task SyncUe4ssModsToServerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_connectedClient is null)
        {
            ConnectionStatusMessage = "Connect to a site first.";
            return;
        }

        if (_settingsService.Current.IcarusContentPath is not { } contentPath)
        {
            ConnectionStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        var localModsFolder = Ue4ssGamePaths.ResolveModsFolder(contentPath);
        if (!Directory.Exists(localModsFolder))
        {
            ConnectionStatusMessage = "No local UE4SS mods folder found.";
            return;
        }

        IsBusy = true;
        try
        {
            var remoteEntries = await TryListRemoteAsync(RemoteModsRootPath);
            if (remoteEntries.Count == 0)
            {
                ConnectionStatusMessage = "Server has no UE4SS Mods folder yet — sync the UE4SS loader to the server first.";
                return;
            }

            var remoteNames = new HashSet<string>(
                remoteEntries.Where(e => e.IsDirectory).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);

            var onInGame = Ue4ssServerFiles.ModsOnInGame(localModsFolder);
            var missing = onInGame.Where(n => !remoteNames.Contains(n)).ToList();

            // Mods that went up on an earlier sync whose mods.txt step failed: on the server, but off.
            var leftOff = await ModsNothingTurnsOnAsync([.. onInGame.Where(remoteNames.Contains)], remoteEntries);
            if (missing.Count == 0 && leftOff.Count == 0)
            {
                ConnectionStatusMessage = "The server already has every UE4SS mod that's on in your game.";
                return;
            }

            var steps = new List<string>();
            if (missing.Count > 0)
            {
                steps.Add($"Upload and turn on in its mods.txt:\n{string.Join('\n', missing.Select(n => $"  - {n}"))}");
            }

            if (leftOff.Count > 0)
            {
                steps.Add($"Turn on in its mods.txt (already on the server, but nothing there turns them on):\n{string.Join('\n', leftOff.Select(n => $"  - {n}"))}");
            }

            var prompt =
                $"This will change UE4SS mods in '{RemoteModsRootPath}' on '{_connectedSite?.Name}':\n\n{string.Join("\n\n", steps)}\n\n"
                + "Nothing else there is touched: other mods and their mods.txt lines stay as they are. Continue?";
            if (!(_dialogService.Confirm(prompt, "Sync UE4SS mods to server", ThemedConfirmSeverity.Warning)))
            {
                return;
            }

            // A mod is "done" only once every file of it is up. When the connection drops part way,
            // the mods already up are still turned on: they used to stay off for good, since the
            // next sync saw their folders and skipped them as "already on the server".
            var done = new List<string>();
            string? brokenOff = null;
            Exception? failure = null;
            foreach (var modName in missing)
            {
                try
                {
                    var localModFolder = Path.Combine(localModsFolder, modName);
                    foreach (var localFile in Directory.GetFiles(localModFolder, "*", SearchOption.AllDirectories))
                    {
                        // The same rule as a local server: what the mod logged in the game and
                        // ArcFission Library's game-session state stay home.
                        var relative = Path.GetRelativePath(localModFolder, localFile);
                        if (!ServerUe4ssSync.IsModFileForServer(localModFolder, relative))
                        {
                            continue;
                        }

                        relative = relative.Replace('\\', '/');
                        await _connectedClient.UploadFileAsync(localFile, $"{RemoteModsRootPath}/{modName}/{relative}");
                    }

                    done.Add(modName);
                }
                catch (Exception ex)
                {
                    brokenOff = modName;
                    failure = ex;
                    break;
                }
            }

            var turnOn = done.Concat(leftOff).ToList();
            if (turnOn.Count > 0)
            {
                // The listing made above says whether the server has a mods.txt: a second listing
                // that failed used to read as "none", and a fresh file with only these mods went up
                // over the server's whole list.
                try
                {
                    await TurnOnInRemoteModsTxtAsync(turnOn, remoteEntries);
                }
                catch (Exception ex)
                {
                    // A dropped connection here used to show only "UE4SS mod sync failed", not which
                    // mods were now on the server but off.
                    ConnectionStatusMessage =
                        $"UE4SS mod sync: {string.Join(", ", turnOn)} {(turnOn.Count == 1 ? "is" : "are")} on the server but NOT turned on: "
                        + $"the server's mods.txt couldn't be updated ({ex.Message.TrimEnd('.')}). Sync again to try once more, "
                        + "or add a line \"Name : 1\" for each to the server's ue4ss\\Mods\\mods.txt in your host's file manager."
                        + (failure is not null ? $" Also, '{brokenOff}' is only partly on the server: delete its folder there, then sync again." : "");
                    _activityLog.Log(ConnectionStatusMessage, ActivityEntryKind.Warning);
                    return;
                }
            }

            var leftOffNote = leftOff.Count > 0
                ? $"Turned on {leftOff.Count} mod(s) already on the server ({string.Join(", ", leftOff)}). "
                : "";
            if (failure is not null)
            {
                ConnectionStatusMessage =
                    $"UE4SS mod sync stopped part way: {failure.Message} "
                    + (done.Count > 0 ? $"{done.Count} mod(s) went up whole and were turned on ({string.Join(", ", done)}). " : "")
                    + leftOffNote
                    + $"'{brokenOff}' is only partly on the server and was not turned on: delete its folder there in the host's file manager, then sync again.";
                _activityLog.Log($"UE4SS mod sync to '{_connectedSite?.Name}' stopped part way at '{brokenOff}': {failure.Message}", ActivityEntryKind.Warning);
                return;
            }

            ConnectionStatusMessage = missing.Count > 0
                ? $"Uploaded {missing.Count} UE4SS mod(s) to '{_connectedSite?.Name}' and turned them on. {leftOffNote}They load when the server restarts."
                : $"{leftOffNote}They load when the server restarts.";
            _activityLog.Log(
                $"UE4SS mod sync to FTP site '{_connectedSite?.Name}': uploaded {missing.Count} ({string.Join(", ", missing)}), turned on {turnOn.Count} ({string.Join(", ", turnOn)}).",
                ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"UE4SS mod sync failed: {ex.Message}";
            _activityLog.Log($"UE4SS mod sync to '{_connectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<IReadOnlyList<FtpEntry>> TryListRemoteAsync(string remotePath)
    {
        try
        {
            return await _connectedClient!.ListDirectoryAsync(remotePath);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Of these mods, already on the server, the ones nothing there turns on: no line in the
    /// server's mods.txt (an explicit "Name : 0" is the admin's choice and stays) and no enabled.txt
    /// in the mod's folder. When the mods.txt or a mod folder can't be read, that mod is left as it
    /// is: only a mod Starlink could see is off gets turned on.
    /// </summary>
    private async Task<IReadOnlyList<string>> ModsNothingTurnsOnAsync(IReadOnlyList<string> onServer, IReadOnlyList<FtpEntry> modsFolderListing)
    {
        if (onServer.Count == 0)
        {
            return [];
        }

        Ue4ssModsTxt list;
        var hasList = modsFolderListing
            .Any(e => !e.IsDirectory && string.Equals(e.Name, Ue4ssModStart.ListFileName, StringComparison.OrdinalIgnoreCase));
        if (hasList)
        {
            string? temp = null;
            try
            {
                temp = Path.GetTempFileName();
                await _connectedClient!.DownloadFileAsync($"{RemoteModsRootPath}/{Ue4ssModStart.ListFileName}", temp);
                list = Ue4ssModsTxt.Load(temp);
            }
            catch (Exception)
            {
                return [];
            }
            finally
            {
                DeleteTempFile(temp);
            }
        }
        else
        {
            list = Ue4ssModsTxt.Parse(null);
        }

        var result = new List<string>();
        foreach (var name in onServer.Where(n => list.IsEnabled(n) is null))
        {
            IReadOnlyList<FtpEntry> folder;
            try
            {
                folder = await _connectedClient!.ListDirectoryAsync($"{RemoteModsRootPath}/{name}");
            }
            catch (Exception)
            {
                continue;
            }

            // A mod folder always holds something: an empty listing is one Starlink couldn't read.
            if (folder.Count > 0
                && !folder.Any(e => !e.IsDirectory && string.Equals(e.Name, Ue4ssModStart.EnabledFileName, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Adds "Name : 1" to the server's mods.txt for each mod (editing its own copy, so its other
    /// lines and order stay; a new file when it has none). The edited file goes up under a
    /// temporary name and is renamed into place, like the merged pak.
    /// </summary>
    private async Task TurnOnInRemoteModsTxtAsync(IReadOnlyList<string> modNames, IReadOnlyList<FtpEntry> modsFolderListing)
    {
        var remoteTxt = $"{RemoteModsRootPath}/{Ue4ssModStart.ListFileName}";
        string? temp = null;
        try
        {
            temp = Path.GetTempFileName();
            var hasOne = modsFolderListing
                .Any(e => !e.IsDirectory && string.Equals(e.Name, Ue4ssModStart.ListFileName, StringComparison.OrdinalIgnoreCase));
            if (hasOne)
            {
                await _connectedClient!.DownloadFileAsync(remoteTxt, temp);
            }
            else
            {
                File.WriteAllText(temp, "");
            }

            var list = Ue4ssModsTxt.Load(temp);
            foreach (var name in modNames)
            {
                list.Set(name, true);
            }

            list.Save(temp);
            try
            {
                await _connectedClient!.UploadFileAsync(temp, UploadingName(remoteTxt));
                await _connectedClient.RenameFileAsync(UploadingName(remoteTxt), remoteTxt);
            }
            catch (FtpOperationRejectedException)
            {
                // The server won't rename: write it straight over the real name.
                await _connectedClient!.UploadFileAsync(temp, remoteTxt);
                try
                {
                    await _connectedClient.DeleteFileAsync(UploadingName(remoteTxt));
                }
                catch (FtpOperationRejectedException)
                {
                    // Not a .txt UE4SS reads; harmless if it stays.
                }
            }
        }
        finally
        {
            DeleteTempFile(temp);
        }
    }
}
