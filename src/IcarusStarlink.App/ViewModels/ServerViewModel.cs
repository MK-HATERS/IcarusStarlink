using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Server;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 8.5: a narrower revival of the spec's original "Server" nav item — FTP file management
/// for a dedicated Icarus server's own mods folder, not a full remote-agent/control page (that was
/// cut from v1 entirely). "Site Manager" (saved connections, FileZilla-style) + a real connection
/// with a directory browser + upload/download/delete.
/// </summary>
public sealed partial class ServerViewModel : ObservableObject
{
    private readonly IFtpSiteStore _siteStore;
    private readonly ICredentialStore _credentialStore;
    private readonly Func<IFtpClient> _ftpClientFactory;

    private IFtpClient? _connectedClient;

    public string Title => "Server";

    public static IReadOnlyList<FtpEncryptionMode> EncryptionModes { get; } = Enum.GetValues<FtpEncryptionMode>();

    // --- Site Manager ---
    public ObservableCollection<FtpSiteProfile> Sites { get; } = [];

    [ObservableProperty]
    private FtpSiteProfile? _selectedSite;

    [ObservableProperty]
    private string _siteNameInput = "";

    [ObservableProperty]
    private string _hostInput = "";

    [ObservableProperty]
    private string _portInput = "21";

    [ObservableProperty]
    private string _usernameInput = "";

    /// <summary>Never pre-filled from a saved site — a saved password is never read back and shown. Leaving this blank while editing an existing site means "keep whatever's already saved".</summary>
    [ObservableProperty]
    private string _passwordInput = "";

    [ObservableProperty]
    private string _remotePathInput = "";

    [ObservableProperty]
    private FtpEncryptionMode _encryptionModeInput;

    [ObservableProperty]
    private string? _siteStatusMessage;

    // --- Connection + directory browser ---
    [ObservableProperty]
    private bool _isConnected;

    public bool IsNotConnected => !IsConnected;

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(IsNotConnected));

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _connectionStatusMessage;

    [ObservableProperty]
    private string _currentRemotePath = "/";

    public ObservableCollection<FtpEntry> RemoteEntries { get; } = [];

    [ObservableProperty]
    private FtpEntry? _selectedRemoteEntry;

    public ServerViewModel(IFtpSiteStore siteStore, ICredentialStore credentialStore, Func<IFtpClient> ftpClientFactory)
    {
        _siteStore = siteStore;
        _credentialStore = credentialStore;
        _ftpClientFactory = ftpClientFactory;

        ReloadSites();
    }

    private void ReloadSites()
    {
        Sites.Clear();
        foreach (var site in _siteStore.GetAll().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            Sites.Add(site);
        }
    }

    partial void OnSelectedSiteChanged(FtpSiteProfile? value)
    {
        if (value is null)
        {
            return;
        }

        SiteNameInput = value.Name;
        HostInput = value.Host;
        PortInput = value.Port.ToString();
        UsernameInput = value.Username;
        PasswordInput = "";
        RemotePathInput = value.RemotePath;
        EncryptionModeInput = value.EncryptionMode;
        SiteStatusMessage = null;
    }

    [RelayCommand]
    private void NewSite()
    {
        SelectedSite = null;
        SiteNameInput = "";
        HostInput = "";
        PortInput = "21";
        UsernameInput = "";
        PasswordInput = "";
        RemotePathInput = "";
        EncryptionModeInput = FtpEncryptionMode.None;
        SiteStatusMessage = null;
    }

    [RelayCommand]
    private void SaveSite()
    {
        if (string.IsNullOrWhiteSpace(SiteNameInput) || string.IsNullOrWhiteSpace(HostInput) || string.IsNullOrWhiteSpace(UsernameInput))
        {
            SiteStatusMessage = "Name, host, and username are required.";
            return;
        }

        if (!int.TryParse(PortInput, out var port) || port is <= 0 or > 65535)
        {
            SiteStatusMessage = "Port must be a number between 1 and 65535.";
            return;
        }

        var id = SelectedSite?.Id ?? Guid.NewGuid();
        var site = new FtpSiteProfile
        {
            Id = id, Name = SiteNameInput, Host = HostInput, Port = port, Username = UsernameInput,
            RemotePath = RemotePathInput, EncryptionMode = EncryptionModeInput,
        };

        try
        {
            _siteStore.Save(site);

            // Blank means "keep whatever's already saved" — only touches the credential when the
            // user actually typed something, so re-saving other fields (e.g. a corrected host)
            // doesn't require re-entering a password that hasn't changed.
            if (!string.IsNullOrEmpty(PasswordInput))
            {
                _credentialStore.Save(CredentialTargets.FtpSite(id), PasswordInput);
                PasswordInput = "";
            }

            ReloadSites();
            SelectedSite = Sites.FirstOrDefault(s => s.Id == id);
            SiteStatusMessage = $"Saved '{site.Name}'.";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a locked/permission-denied ftp_sites.json, or
            // a Credential Manager failure, shows a status message instead of crashing the app.
            SiteStatusMessage = $"Couldn't save site: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteSite()
    {
        if (SelectedSite is not { } site)
        {
            SiteStatusMessage = "Select a site first.";
            return;
        }

        try
        {
            _siteStore.Delete(site.Id);
            _credentialStore.Delete(CredentialTargets.FtpSite(site.Id));
            ReloadSites();
            NewSite();
            SiteStatusMessage = $"Deleted '{site.Name}'.";
        }
        catch (Exception ex)
        {
            SiteStatusMessage = $"Couldn't delete site: {ex.Message}";
        }
    }

    /// <summary>
    /// "Reconnect, not relogin" per the spec — if a password was saved on a previous Connect (or
    /// via Save), this needs nothing further from the user at all. Typing a password here first
    /// always saves it, matching Save's own behavior, so connecting once is enough to make every
    /// future connect password-free too.
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedSite is not { } site)
        {
            ConnectionStatusMessage = "Select or save a site first.";
            return;
        }

        string password;
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

        IsConnecting = true;
        ConnectionStatusMessage = "Connecting…";

        var client = _ftpClientFactory();
        try
        {
            await client.ConnectAsync(site, password);
            _connectedClient = client;
            IsConnected = true;
            ConnectionStatusMessage = $"Connected to '{site.Name}'.";
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
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_connectedClient is null)
        {
            return;
        }

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
            await _connectedClient.DisposeAsync();
            _connectedClient = null;
            IsConnected = false;
            RemoteEntries.Clear();
            ConnectionStatusMessage = "Disconnected.";
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

    [RelayCommand]
    private Task Refresh() => LoadDirectoryAsync(CurrentRemotePath);

    [RelayCommand]
    private Task NavigateInto(FtpEntry? entry)
    {
        if (entry is null || !entry.IsDirectory)
        {
            return Task.CompletedTask;
        }

        return LoadDirectoryAsync(CombineRemotePath(CurrentRemotePath, entry.Name));
    }

    [RelayCommand]
    private Task NavigateUp()
    {
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

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (_connectedClient is null)
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
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Upload failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
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
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes a file on the real remote server — asks for confirmation first, since this isn't something a later Disable/re-Apply step can undo the way local UE4SS mod moves can.</summary>
    [RelayCommand]
    private async Task DeleteRemoteAsync()
    {
        if (_connectedClient is null || SelectedRemoteEntry is not { IsDirectory: false } entry)
        {
            ConnectionStatusMessage = "Select a file first.";
            return;
        }

        var result = MessageBox.Show(
            $"Delete '{entry.Name}' from the server? This can't be undone from here.",
            "Delete remote file", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _connectedClient.DeleteFileAsync(CombineRemotePath(CurrentRemotePath, entry.Name));
            await LoadDirectoryAsync(CurrentRemotePath);
            ConnectionStatusMessage = $"Deleted '{entry.Name}'.";
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
