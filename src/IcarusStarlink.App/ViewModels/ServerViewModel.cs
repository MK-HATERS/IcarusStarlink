using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO;
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
    private readonly IActivityLog _activityLog;
    private readonly ISettingsService _settingsService;
    private readonly IUe4ssModRepository _ue4ssModRepository;
    private readonly string _outputPakPath;
    private readonly string _pakManifestPath;

    // Confirmed against the user's own real dedicated server (SurvivalServers): the FTP login
    // root is NOT the game project root — it's one level up, with the actual Content\/Binaries\
    // tree sitting under a fixed "Icarus" subfolder (alongside IcarusServer.exe, a "Saved" folder,
    // etc.) — SurvivalServers' own per-game-type convention, not something this user configured.
    // Below that, it's an exact mirror of the local layout, just with forward slashes. Fixed
    // rather than a per-site field, per the user's own call ("assume a fixed relative path should
    // be fine").
    private const string RemoteModsPath = "Icarus/Content/Paks/mods";
    private const string RemoteWin64Path = "Icarus/Binaries/Win64";
    private const string RemoteLoaderPath = "Icarus/Binaries/Win64/ue4ss";
    private const string RemoteModsRootPath = "Icarus/Binaries/Win64/ue4ss/Mods";

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

    /// <summary>Drives the "+ New site" Expander — collapsed by default (nothing to fill in until the user asks), opened automatically by NewSite/selecting a site, two-way bound so a user can also collapse it manually without losing anything typed.</summary>
    [ObservableProperty]
    private bool _isSiteFormOpen;

    // --- Connection + directory browser ---
    [ObservableProperty]
    private bool _isConnected;

    // Also false while IsConnecting — the Connect button binds IsEnabled to this, so a slow
    // handshake can't be double-clicked into creating a second concurrent IFtpClient.
    public bool IsNotConnected => !IsConnected && !IsConnecting;

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(IsNotConnected));
    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(IsNotConnected));

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

    public ServerViewModel(
        IFtpSiteStore siteStore,
        ICredentialStore credentialStore,
        Func<IFtpClient> ftpClientFactory,
        IActivityLog activityLog,
        ISettingsService settingsService,
        IUe4ssModRepository ue4ssModRepository,
        string outputPakPath)
    {
        _siteStore = siteStore;
        _credentialStore = credentialStore;
        _ftpClientFactory = ftpClientFactory;
        _activityLog = activityLog;
        _settingsService = settingsService;
        _ue4ssModRepository = ue4ssModRepository;
        _outputPakPath = outputPakPath;
        _pakManifestPath = Path.Combine(Path.GetDirectoryName(outputPakPath)!, InstallManifestNames.PakManifest);

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
        IsSiteFormOpen = true;
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
        IsSiteFormOpen = true;
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
            try
            {
                await client.ConnectAsync(site, password);
            }
            catch (FtpUntrustedCertificateException certEx)
            {
                // A real "trust this certificate?" prompt — the same convention FileZilla/WinSCP
                // use for exactly this case (common on budget game-server hosts presenting a
                // self-signed cert), rather than just failing with a generic TLS error.
                var trust = MessageBox.Show(
                    $"{certEx.Message}\n\nSubject: {certEx.Subject}\nIssuer: {certEx.Issuer}\nThumbprint: {certEx.Thumbprint}\n\n"
                    + $"Trust this certificate for '{site.Name}' and connect anyway? Only do this if you recognize this server.",
                    "Untrusted certificate", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (trust != MessageBoxResult.Yes)
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
            _activityLog.Log($"Disconnected from FTP site '{SelectedSite?.Name}'.");
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
            _activityLog.Log($"Uploaded '{Path.GetFileName(dialog.FileName)}' to '{SelectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Upload failed: {ex.Message}";
            _activityLog.Log($"Upload to '{SelectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
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
            _activityLog.Log($"Downloaded '{entry.Name}' from '{SelectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Download failed: {ex.Message}";
            _activityLog.Log($"Download of '{entry.Name}' from '{SelectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
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
            _activityLog.Log($"Deleted '{entry.Name}' from '{SelectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Delete failed: {ex.Message}";
            _activityLog.Log($"Delete of '{entry.Name}' on '{SelectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Uploads the app's own last-built merged pak (Merge &amp; Install's own Staged_Build output —
    /// the same file "Install" copies into the LOCAL game folder) to the server's own mods folder,
    /// replacing whatever's there. The server's mods folder is meant to hold exactly one active
    /// merged pak, so this clears it out first — but always names every file it's about to delete
    /// (and upload) in the confirmation prompt, per the user's own explicit ask, rather than a bare
    /// "are you sure?".
    /// </summary>
    [RelayCommand]
    private async Task InstallPakToServerAsync()
    {
        if (_connectedClient is null)
        {
            ConnectionStatusMessage = "Connect to a site first.";
            return;
        }

        if (!File.Exists(_outputPakPath))
        {
            ConnectionStatusMessage = "No built pak yet — run Rebuild on Merge & Install first.";
            return;
        }

        IsBusy = true;
        try
        {
            var existing = (await _connectedClient.ListDirectoryAsync(RemoteModsPath))
                .Where(e => !e.IsDirectory)
                .ToList();

            var toUpload = new List<string> { Path.GetFileName(_outputPakPath) };
            if (File.Exists(_pakManifestPath))
            {
                toUpload.Add(Path.GetFileName(_pakManifestPath));
            }

            var deleteList = existing.Count > 0
                ? "delete:\n" + string.Join('\n', existing.Select(e => $"  - {e.Name}")) + "\n\nthen "
                : "";
            var prompt =
                $"This will {deleteList}upload:\n{string.Join('\n', toUpload.Select(n => $"  - {n}"))}\n\n"
                + $"to '{RemoteModsPath}' on '{SelectedSite?.Name}'. Continue?";

            if (MessageBox.Show(prompt, "Install merged pak to server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var entry in existing)
            {
                await _connectedClient.DeleteFileAsync($"{RemoteModsPath}/{entry.Name}");
            }

            await _connectedClient.UploadFileAsync(_outputPakPath, $"{RemoteModsPath}/{Path.GetFileName(_outputPakPath)}");
            if (File.Exists(_pakManifestPath))
            {
                await _connectedClient.UploadFileAsync(_pakManifestPath, $"{RemoteModsPath}/{Path.GetFileName(_pakManifestPath)}");
            }

            ConnectionStatusMessage = $"Installed the merged pak to '{SelectedSite?.Name}'.";
            _activityLog.Log($"Installed the merged pak to FTP site '{SelectedSite?.Name}' (replaced {existing.Count} file(s)).", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Install to server failed: {ex.Message}";
            _activityLog.Log($"Install to server '{SelectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
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
    [RelayCommand]
    private async Task SyncUe4ssLoaderToServerAsync()
    {
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
        var localLogPath = Ue4ssGamePaths.ResolveLoaderLogPath(contentPath);
        if (!File.Exists(localLogPath))
        {
            ConnectionStatusMessage = "UE4SS isn't installed locally — nothing to sync.";
            return;
        }

        var localVersion = Ue4ssLogVersionParser.Parse(File.ReadLines(localLogPath));

        IsBusy = true;
        var tempLogPath = Path.GetTempFileName();
        try
        {
            string? remoteVersion = null;
            try
            {
                await _connectedClient.DownloadFileAsync($"{RemoteLoaderPath}/UE4SS.log", tempLogPath);
                remoteVersion = Ue4ssLogVersionParser.Parse(File.ReadLines(tempLogPath));
            }
            catch (Exception)
            {
                // No UE4SS.log on the server yet (or unreadable) — treated as "not installed",
                // same as the local Ue4ssLoaderInstallService.GetStatus contract.
            }

            if (remoteVersion is not null && string.Equals(remoteVersion, localVersion, StringComparison.OrdinalIgnoreCase))
            {
                ConnectionStatusMessage = $"Server already has UE4SS v{remoteVersion} — nothing to do.";
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
                + $"'{SelectedSite?.Name}', replacing its current loader ({(remoteVersion is null ? "not installed" : $"v{remoteVersion}")}). "
                + $"The server's own Mods folder{(remoteHasSettings ? " and its existing UE4SS-settings.ini" : "")} won't be touched. Continue?";
            if (MessageBox.Show(prompt, "Sync UE4SS loader to server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var localFile in localFiles)
            {
                var relative = Path.GetRelativePath(localLoaderFolder, localFile).Replace('\\', '/');
                await _connectedClient.UploadFileAsync(localFile, $"{RemoteLoaderPath}/{relative}");
            }

            await _connectedClient.UploadFileAsync(Ue4ssGamePaths.ResolveDwmapiPath(contentPath), $"{RemoteWin64Path}/dwmapi.dll");

            ConnectionStatusMessage = $"Synced UE4SS v{localVersion} to '{SelectedSite?.Name}'.";
            _activityLog.Log($"Synced UE4SS loader (v{localVersion}) to FTP site '{SelectedSite?.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"UE4SS sync failed: {ex.Message}";
            _activityLog.Log($"UE4SS loader sync to '{SelectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
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
    [RelayCommand]
    private async Task SyncUe4ssModsToServerAsync()
    {
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
                $"This will upload the following UE4SS mod(s) to '{RemoteModsRootPath}' on '{SelectedSite?.Name}' "
                + $"(nothing already there is touched):\n{string.Join('\n', missing.Select(n => $"  - {n}"))}\n\nContinue?";
            if (MessageBox.Show(prompt, "Sync UE4SS mods to server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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

            ConnectionStatusMessage = $"Uploaded {missing.Count} UE4SS mod(s) to '{SelectedSite?.Name}'.";
            _activityLog.Log($"Uploaded {missing.Count} UE4SS mod(s) to FTP site '{SelectedSite?.Name}': {string.Join(", ", missing)}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"UE4SS mod sync failed: {ex.Message}";
            _activityLog.Log($"UE4SS mod sync to '{SelectedSite?.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
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
