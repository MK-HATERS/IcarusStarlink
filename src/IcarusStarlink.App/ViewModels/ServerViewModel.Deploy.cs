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

    /// <summary>
    /// Uploads the app's own last-built merged pak (Merge &amp; Install's own Staged_Build output —
    /// the same file "Install" copies into the LOCAL game folder) to the server's own mods folder,
    /// replacing whatever's there. The server's mods folder is meant to hold exactly one active
    /// merged pak, so this clears it out first — but always names every file it's about to delete
    /// (and upload) in the confirmation prompt, per the user's own explicit ask, rather than a bare
    /// "are you sure?".
    ///
    /// A site already known (from a past attempt) not to support delete skips trying it at all and
    /// instead warns UP FRONT, before ever touching the server, that old files will remain — the
    /// user's own explicit preference over silently uploading first and reporting the leftover
    /// files only afterward, since a dedicated server actively serving players is higher-stakes
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
            var toUpload = new List<string> { Path.GetFileName(_outputPakPath) };
            if (File.Exists(_pakManifestPath))
            {
                toUpload.Add(Path.GetFileName(_pakManifestPath));
            }

            // ArcFission Library's own data pak stays: the server's library writes it again anyway.
            // A leftover upload from an earlier attempt that broke off is replaced, not listed.
            var existing = (await _connectedClient.ListDirectoryAsync(RemoteModsPath))
                .Where(e => !e.IsDirectory && !IcarusStarlink.PakIO.InstallManifestNames.IsArcFissionDataPak(e.Name))
                .Where(e => !toUpload.Any(name => string.Equals(e.Name, UploadingName(name), StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // _connectedSite, not SelectedSite: this gates a real delete attempt against the LIVE
            // connection, and must reflect what's actually known about the site being acted on, not
            // whatever row happens to be selected in the list right now (see _connectedSite's own
            // doc comment on why those two can drift).
            var knownBlocked = existing.Count > 0 && _connectedSite?.SupportsDelete == false;
            string prompt;
            if (knownBlocked)
            {
                prompt =
                    $"'{_connectedSite?.Name}' doesn't allow deleting or replacing files via FTP, so the existing file(s) below will remain after this upload:\n"
                    + string.Join('\n', existing.Select(e => $"  - {e.Name}"))
                    + $"\n\nUpload will add:\n{string.Join('\n', toUpload.Select(n => $"  - {n}"))}\n\n"
                    + $"You'll need to remove the old file(s) yourself via {_connectedSite?.Name}'s own file manager afterward — otherwise the server may end up loading multiple conflicting merged paks. Upload anyway?";
            }
            else
            {
                var deleteList = existing.Count > 0
                    ? "\n\nOnce it is fully uploaded, these are deleted:\n" + string.Join('\n', existing.Select(e => $"  - {e.Name}"))
                    : "";
                prompt =
                    $"This will upload:\n{string.Join('\n', toUpload.Select(n => $"  - {n}"))}\n\n"
                    + $"to '{RemoteModsPath}' on '{_connectedSite?.Name}'.{deleteList}\n\nContinue?";
            }

            if (!(_dialogService.Confirm(prompt, "Install merged pak to server", ThemedConfirmSeverity.Warning)))
            {
                return;
            }

            var localFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFileName(_outputPakPath)] = _outputPakPath,
            };
            if (File.Exists(_pakManifestPath))
            {
                localFor[Path.GetFileName(_pakManifestPath)] = _pakManifestPath;
            }

            var deleteFailures = new List<string>();
            if (knownBlocked)
            {
                // Nothing can be deleted (or, likely, renamed) there: upload straight over the names.
                deleteFailures.AddRange(existing.Select(e => e.Name));
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
                // deleted and the new one half there (an audit finding).
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

                if (existing.Count > 0)
                {
                    RememberDeleteCapability(deleteFailures.Count == 0);
                }

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
    /// the server's own Mods\ folder or its UE4SS-settings.ini if one already exists there, so a
    /// loader update can't clobber whatever's already configured/running on the server.
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
        var tempLogPath = Path.GetTempFileName();
        try
        {
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

            var remoteHasSettings = (await TryListRemoteAsync(RemoteLoaderPath))
                .Any(e => !e.IsDirectory && string.Equals(e.Name, "UE4SS-settings.ini", StringComparison.OrdinalIgnoreCase));

            var localFiles = Directory.GetFiles(localLoaderFolder, "*", SearchOption.AllDirectories)
                .Where(f => !IsUnderModsFolder(localLoaderFolder, f))
                .Where(f => !(remoteHasSettings && Path.GetFileName(f).Equals("UE4SS-settings.ini", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var prompt =
                $"This will upload {localFiles.Count} loader file(s) (UE4SS.dll, dwmapi.dll, etc.) to '{RemoteWin64Path}' on "
                + $"'{_connectedSite?.Name}', replacing its current loader ({(remoteVersion is null ? "not installed" : Ue4ssBuild.Describe(remoteVersion, remoteCommit))}) with yours ({localLabel}). "
                + $"The server's own Mods folder{(remoteHasSettings ? " and its existing UE4SS-settings.ini" : "")} won't be touched. Continue?";
            if (!(_dialogService.Confirm(prompt, "Sync UE4SS loader to server", ThemedConfirmSeverity.Warning)))
            {
                return;
            }

            foreach (var localFile in localFiles)
            {
                var relative = Path.GetRelativePath(localLoaderFolder, localFile).Replace('\\', '/');
                await _connectedClient.UploadFileAsync(localFile, $"{RemoteLoaderPath}/{relative}");
            }

            await _connectedClient.UploadFileAsync(Ue4ssGamePaths.ResolveDwmapiPath(contentPath), $"{RemoteWin64Path}/dwmapi.dll");

            ConnectionStatusMessage = $"Synced UE4SS {localLabel} to '{_connectedSite?.Name}'.";
            _activityLog.Log($"Synced UE4SS loader ({localLabel}) to FTP site '{_connectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"UE4SS sync failed: {ex.Message}";
            _activityLog.Log($"UE4SS loader sync to '{_connectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            File.Delete(tempLogPath);
            IsBusy = false;
        }
    }

    /// <summary>
    /// Uploads whichever locally-enabled UE4SS mods (the game's own real Mods\ folder — not this
    /// app's staging) aren't already present on the server, by folder name. Additive only, mirroring
    /// the same "never overwrite/remove something already there" philosophy the local loader
    /// install and enable/disable state service both already use — this never touches or removes a
    /// mod already on the server, including the framework's own built-ins.
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

            var localNames = _ue4ssModRepository.ListInstalledInGame(localModsFolder);
            var missing = localNames.Where(n => !remoteNames.Contains(n)).ToList();
            if (missing.Count == 0)
            {
                ConnectionStatusMessage = "Server already has every locally-enabled UE4SS mod.";
                return;
            }

            var prompt =
                $"This will upload the following UE4SS mod(s) to '{RemoteModsRootPath}' on '{_connectedSite?.Name}' "
                + $"(nothing already there is touched):\n{string.Join('\n', missing.Select(n => $"  - {n}"))}\n\nContinue?";
            if (!(_dialogService.Confirm(prompt, "Sync UE4SS mods to server", ThemedConfirmSeverity.Warning)))
            {
                return;
            }

            foreach (var modName in missing)
            {
                var localModFolder = Path.Combine(localModsFolder, modName);
                foreach (var localFile in Directory.GetFiles(localModFolder, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(localModFolder, localFile).Replace('\\', '/');
                    await _connectedClient.UploadFileAsync(localFile, $"{RemoteModsRootPath}/{modName}/{relative}");
                }
            }

            ConnectionStatusMessage = $"Uploaded {missing.Count} UE4SS mod(s) to '{_connectedSite?.Name}'.";
            _activityLog.Log($"Uploaded {missing.Count} UE4SS mod(s) to FTP site '{_connectedSite?.Name}': {string.Join(", ", missing)}.", ActivityEntryKind.Success);
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

    private static bool IsUnderModsFolder(string loaderFolder, string filePath)
    {
        var relative = Path.GetRelativePath(loaderFolder, filePath);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment.Equals("Mods", StringComparison.OrdinalIgnoreCase);
    }
}
