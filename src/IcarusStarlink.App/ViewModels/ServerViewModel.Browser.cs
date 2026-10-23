using System.IO;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Server;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The live connection and its file browser: connect, list, upload, download, delete.</summary>
public sealed partial class ServerViewModel
{
    /// <summary>
    /// "Reconnect, not relogin" per the spec — if a password was saved on a previous Connect (or
    /// via Save), this needs nothing further from the user at all. Typing a password here first
    /// always saves it, matching Save's own behavior, so connecting once is enough to make every
    /// future connect password-free too.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ConnectAsync()
    {
        // IsEnabled on the Connect button is bound to IsNotConnected, which only flips false once
        // this method has already returned — without this guard, a second click during a slow
        // handshake would create and connect a second IFtpClient, and whichever finished second
        // would silently overwrite _connectedClient, orphaning the first (never disposed, never
        // reachable by Disconnect again).
        if (IsConnecting)
        {
            return;
        }

        if (SelectedSite is not { } site)
        {
            ConnectionStatusMessage = "Select or save a site first.";
            return;
        }

        string password;
        try
        {
            if (!string.IsNullOrEmpty(PasswordInput))
            {
                password = PasswordInput;
                _credentialStore.Save(CredentialTargets.FtpSite(site.Id), password);
                PasswordInput = "";
            }
            else if (_credentialStore.Read(CredentialTargets.FtpSite(site.Id)) is { } savedPassword)
            {
                password = savedPassword;
            }
            else
            {
                ConnectionStatusMessage = "No saved password for this site — type one and Connect again.";
                return;
            }
        }
        catch (Exception ex)
        {
            // A Credential Manager failure used to escape the command (it flows exceptions to the
            // task scheduler), so Connect silently did nothing.
            ConnectionStatusMessage = $"Couldn't read or save this site's password in Windows Credential Manager: {ex.Message}";
            return;
        }

        IsConnecting = true;
        ConnectionStatusMessage = "Connecting…";

        var client = _ftpClientFactory();
        try
        {
            try
            {
                await client.ConnectAsync(site, password);
            }
            catch (FtpUntrustedCertificateException certEx)
            {
                // A real "trust this certificate?" prompt — the same convention FileZilla/WinSCP
                // use for exactly this case (common on budget game-server hosts presenting a
                // self-signed cert), rather than just failing with a generic TLS error.
                var trust = _dialogService.Confirm(
                    $"{certEx.Message}\n\nSubject: {certEx.Subject}\nIssuer: {certEx.Issuer}\nThumbprint: {certEx.Thumbprint}\n\n"
                    + $"Trust this certificate for '{site.Name}' and connect anyway? Only do this if you recognize this server.",
                    "Untrusted certificate", ThemedConfirmSeverity.Warning);
                if (!(trust))
                {
                    await client.DisposeAsync();
                    ConnectionStatusMessage = "Connection cancelled — certificate not trusted.";
                    _activityLog.Log($"Declined an untrusted TLS certificate for '{site.Name}'.", ActivityEntryKind.Warning);
                    return;
                }

                site.TrustedCertificateThumbprint = certEx.Thumbprint;
                _siteStore.Save(site);
                await client.ConnectAsync(site, password);
            }

            _connectedClient = client;
            _connectedSite = site;
            IsConnected = true;
            ConnectionStatusMessage = $"Connected to '{site.Name}'.";
            _activityLog.Log($"Connected to FTP site '{site.Name}'.", ActivityEntryKind.Success);
            await LoadDirectoryAsync(string.IsNullOrWhiteSpace(site.RemotePath) ? "/" : site.RemotePath);
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a wrong password, unreachable host, or
            // firewall/port issue shows a status message, not a crash. The client itself is only
            // ever assigned to _connectedClient (and thus only ever disposed via Disconnect) once
            // ConnectAsync has actually succeeded — dispose it here too, or a failed attempt leaks
            // its underlying socket/TLS session.
            await client.DisposeAsync();
            ConnectionStatusMessage = $"Couldn't connect: {ex.Message}";
            _activityLog.Log($"Couldn't connect to FTP site '{site.Name}': {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DisconnectAsync()
    {
        // Never dispose out from under a still-running Upload/Download/Delete/etc. — see the
        // IsBusy doc comment above.
        if (_connectedClient is null || IsBusy)
        {
            return;
        }

        // This command touches the shared _connectedClient the same way every other FTP command
        // does, so it needs the same IsBusy=true guard around its own body — without it, every
        // IsEnabled="{Binding IsNotBusy}" button stayed clickable for the full duration of the two
        // awaits below, and _connectedClient stayed non-null until the finally block, so a command
        // like Refresh could genuinely race DisconnectAsync's own DisconnectAsync()/DisposeAsync()
        // calls on the same non-thread-safe client instance.
        IsBusy = true;
        try
        {
            await _connectedClient.DisconnectAsync();
        }
        catch (Exception)
        {
            // Best-effort — the connection may already be gone (server closed it, network drop);
            // either way we're about to dispose and drop the client below.
        }
        finally
        {
            var disconnectedSiteName = _connectedSite?.Name;
            try
            {
                await _connectedClient.DisposeAsync();
            }
            catch (Exception)
            {
                // Best-effort as above: a failed dispose must not leave the page locked busy
                // with a dead client still "connected".
            }

            _connectedClient = null;
            _connectedSite = null;
            IsConnected = false;
            RemoteEntries.Clear();
            ConnectionStatusMessage = "Disconnected.";
            _activityLog.Log($"Disconnected from FTP site '{disconnectedSiteName}'.");
            IsBusy = false;
        }
    }

    private async Task LoadDirectoryAsync(string remotePath)
    {
        if (_connectedClient is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var entries = await _connectedClient.ListDirectoryAsync(remotePath);
            CurrentRemotePath = remotePath;
            RemoteEntries.Clear();
            foreach (var entry in entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                RemoteEntries.Add(entry);
            }
            ConnectionStatusMessage = null;
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Couldn't list '{remotePath}': {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task Refresh() => IsBusy ? Task.CompletedTask : LoadDirectoryAsync(CurrentRemotePath);

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task NavigateInto(FtpEntry? entry)
    {
        if (IsBusy || entry is null || !entry.IsDirectory)
        {
            return Task.CompletedTask;
        }

        return LoadDirectoryAsync(CombineRemotePath(CurrentRemotePath, entry.Name));
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task NavigateUp()
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }

        var trimmed = CurrentRemotePath.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var parent = lastSlash <= 0 ? "/" : trimmed[..lastSlash];
        return LoadDirectoryAsync(parent);
    }

    private static string CombineRemotePath(string basePath, string name)
    {
        var trimmed = basePath.TrimEnd('/');
        return $"{trimmed}/{name}";
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task UploadAsync()
    {
        if (_connectedClient is null || IsBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = "Select a file to upload" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var remotePath = CombineRemotePath(CurrentRemotePath, Path.GetFileName(dialog.FileName));
            await _connectedClient.UploadFileAsync(dialog.FileName, remotePath);
            await LoadDirectoryAsync(CurrentRemotePath);
            ConnectionStatusMessage = $"Uploaded '{Path.GetFileName(dialog.FileName)}'.";
            _activityLog.Log($"Uploaded '{Path.GetFileName(dialog.FileName)}' to '{_connectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Upload failed: {ex.Message}";
            _activityLog.Log($"Upload to '{_connectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DownloadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_connectedClient is null || SelectedRemoteEntry is not { IsDirectory: false } entry)
        {
            ConnectionStatusMessage = "Select a file first.";
            return;
        }

        var dialog = new SaveFileDialog { Title = "Save downloaded file", FileName = entry.Name };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var remotePath = CombineRemotePath(CurrentRemotePath, entry.Name);
            await _connectedClient.DownloadFileAsync(remotePath, dialog.FileName);
            ConnectionStatusMessage = $"Downloaded '{entry.Name}'.";
            _activityLog.Log($"Downloaded '{entry.Name}' from '{_connectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Download failed: {ex.Message}";
            _activityLog.Log($"Download of '{entry.Name}' from '{_connectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes a file on the real remote server — asks for confirmation first, since this isn't something a later Disable/re-Apply step can undo the way local UE4SS mod moves can.</summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DeleteRemoteAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_connectedClient is null || SelectedRemoteEntry is not { IsDirectory: false } entry)
        {
            ConnectionStatusMessage = "Select a file first.";
            return;
        }

        var result = _dialogService.Confirm(
            $"Delete '{entry.Name}' from the server? This can't be undone from here.",
            "Delete remote file", ThemedConfirmSeverity.Warning);
        if (!(result))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _connectedClient.DeleteFileAsync(CombineRemotePath(CurrentRemotePath, entry.Name));
            RememberDeleteCapability(true);
            await LoadDirectoryAsync(CurrentRemotePath);
            ConnectionStatusMessage = $"Deleted '{entry.Name}'.";
            _activityLog.Log($"Deleted '{entry.Name}' from '{_connectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (FtpOperationRejectedException ex)
        {
            RememberDeleteCapability(false);
            ConnectionStatusMessage = $"Delete failed — this host doesn't allow it: {ex.ServerMessage}";
            _activityLog.Log($"Delete of '{entry.Name}' on '{_connectedSite?.Name}' was rejected by the server: {ex.ServerMessage}", ActivityEntryKind.Warning);
        }
        catch (Exception ex)
        {
            // A network/connection failure tells us nothing about this site's own delete
            // capability — only a real server rejection (caught above) does, so SupportsDelete is
            // deliberately left unchanged here.
            ConnectionStatusMessage = $"Delete failed: {ex.Message}";
            _activityLog.Log($"Delete of '{entry.Name}' on '{_connectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Persists whether this site's own FTP account can delete/overwrite an existing file,
    /// once actually confirmed either way — shared by the single-file Delete button and Install
    /// merged pak to server, so either one learning the answer benefits the other.
    ///
    /// Written against _connectedSite, not SelectedSite: the capability was just learned from a
    /// real delete result against the LIVE connection, and SelectedSite can have drifted to a
    /// different saved site in the list without disconnecting (see _connectedSite's own doc
    /// comment) — writing to SelectedSite in that case would silently persist an untested fact onto
    /// the wrong site's profile while discarding the real result.</summary>
    private void RememberDeleteCapability(bool supportsDelete)
    {
        if (_connectedSite is null || _connectedSite.SupportsDelete == supportsDelete)
        {
            return;
        }

        _connectedSite.SupportsDelete = supportsDelete;

        // Saved onto the CURRENT stored profile, not the object captured at Connect (an audit
        // finding): _connectedSite predates any Save the user made since connecting, so writing it
        // back wholesale silently reverted a just-saved ModsPath/Host/Port edit to its pre-connect
        // value. Only the learned fact is applied; everything else stays as last saved.
        var stored = _siteStore.GetAll().FirstOrDefault(s => s.Id == _connectedSite.Id) ?? _connectedSite;
        stored.SupportsDelete = supportsDelete;
        _siteStore.Save(stored);
        OnPropertyChanged(nameof(HasKnownDeleteRestriction));
    }
}
