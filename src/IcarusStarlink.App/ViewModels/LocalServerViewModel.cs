using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Server page's "Local server" tab: dedicated servers on THIS PC. Phase 1 of the server plan —
/// adopt an existing install, edit its launch options and ServerSettings.ini, start / stop /
/// restart it, and follow its log live. (Installing and updating through SteamCMD is phase 2.)
/// The ini is the source of truth for in-game settings: the form loads from it on select and
/// writes back to it on Save, keeping every line it doesn't know about.
/// </summary>
public sealed partial class LocalServerViewModel : ObservableObject
{
    private const int MaxLogLines = 4000;

    private readonly IDedicatedServerProfileStore _store;
    private readonly IDedicatedServerProcessService _processes;
    private readonly IDialogService _dialogService;
    private readonly IActivityLog _activityLog;
    private readonly Func<string, string?> _pickFolder;
    private readonly Func<string?> _pickSteamCmdExe;
    private readonly ISteamCmdService? _steamCmd;
    private readonly Core.Settings.ISettingsService? _settings;
    private readonly string? _mergedPakPath;
    private readonly string _backupsRoot;
    private readonly DispatcherTimer? _timer;
    private readonly IFirewallService? _firewall;
    private readonly List<string> _allLogLines = [];
    private LogTailer? _tailer;

    public LocalServerViewModel(
        IDedicatedServerProfileStore store, IDedicatedServerProcessService processes, IDialogService dialogService,
        IActivityLog activityLog, ISteamCmdService? steamCmd = null, Core.Settings.ISettingsService? settings = null,
        string? mergedPakPath = null, string? backupsRoot = null, Func<string, string?>? pickFolder = null, Func<string?>? pickSteamCmdExe = null, bool startTimer = true,
        IFirewallService? firewall = null, string? dataFolder = null)
    {
        // Set before SelectedProfile below: selecting a profile loads its CreateProspect, which
        // reads (and caches) ProspectChoices from this folder. As an init property it arrived
        // too late, so the dropdown only ever held "Don't create one".
        DataFolder = dataFolder;
        _firewall = firewall;
        _steamCmd = steamCmd;
        _settings = settings;
        _mergedPakPath = mergedPakPath;
        _backupsRoot = backupsRoot ?? Path.Combine(Path.GetTempPath(), "IcarusStarlink", "ServerBackups");
        _store = store;
        _processes = processes;
        _dialogService = dialogService;
        _activityLog = activityLog;
        _pickFolder = pickFolder ?? PickFolderWithDialog;
        _pickSteamCmdExe = pickSteamCmdExe ?? PickSteamCmdWithDialog;
        WorldSettings = new WorldSettingsViewModel(
            () => SelectedProfile is { } p ? DedicatedServerLayout.ProspectsFolder(p.InstallFolder, p.SavedDirSuffix) : null,
            () => DataFolder,
            () => IsRunning ? "Stop the server first: it rewrites its worlds when it shuts down, which would undo this." : null,
            Path.Combine(_backupsRoot, "WorldSettings"),
            activityLog);

        foreach (var profile in store.GetAll())
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault();

        // Refresh game data re-extracts D_ProspectList — rebuild the prospect choices from it.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((LocalServerViewModel)recipient).ReloadProspectChoices());

        // A new merged pak was built — "Install my merged pak" and its summary follow it.
        WeakReferenceMessenger.Default.Register<MergedPakBuiltMessage>(this, (recipient, _) => ((LocalServerViewModel)recipient).RefreshMergedPak());

        if (startTimer && Application.Current is not null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Normal, System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                Tick();
                AutomationTick();
            };
            _timer.Start();
            _ = StartServersMarkedForStartupAsync();
        }
    }

    public ObservableCollection<DedicatedServerProfile> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile), nameof(HasNoProfile))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(SaveSettingsCommand), nameof(RemoveProfileCommand))]
    private DedicatedServerProfile? _selectedProfile;

    public bool HasProfile => SelectedProfile is not null;

    public bool HasNoProfile => SelectedProfile is null;

    private bool _suppressFormReload;

    partial void OnSelectedProfileChanged(DedicatedServerProfile? value)
    {
        if (_suppressFormReload)
        {
            return;
        }

        LoadForm();
        AttachLog();
        Tick();
        RefreshServerExtras();
        LoadFirewall();
        RefreshOverview();
    }

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _validationMessage;

    // ---- Launch options (stored in the profile) ----

    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _steamServerName = "";
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _queryPortText = "";
    [ObservableProperty] private string _multihome = "";
    [ObservableProperty] private string _savedDirSuffix = "";
    [ObservableProperty] private string _logFileName = "";

    public string InstallFolder => SelectedProfile?.InstallFolder ?? "";

    // ---- ServerSettings.ini (stored in the server's own file) ----

    [ObservableProperty] private string _joinPassword = "";
    [ObservableProperty] private string _adminPassword = "";
    [ObservableProperty] private string _maxPlayersText = "";
    [ObservableProperty] private string _shutdownIfNotJoinedForText = "";
    [ObservableProperty] private string _shutdownIfEmptyForText = "";
    [ObservableProperty] private string _gameSaveFrequencyText = "";
    [ObservableProperty] private string _loadProspect = "";
    [ObservableProperty] private string _createProspect = "";
    [ObservableProperty] private bool? _allowNonAdminsToLaunchProspects;
    [ObservableProperty] private bool? _allowNonAdminsToDeleteProspects;
    [ObservableProperty] private bool? _resumeProspect;
    [ObservableProperty] private bool? _saveGameOnExit;
    [ObservableProperty] private bool? _fiberFoliageRespawn;
    [ObservableProperty] private bool? _largeStonesRespawn;

    /// <summary>Written by the server — shown, never edited.</summary>
    [ObservableProperty] private string? _lastProspectName;

    public ObservableCollection<string> Prospects { get; } = [];

    /// <summary>The World settings tab: each saved world's custom settings.</summary>
    public WorldSettingsViewModel WorldSettings { get; }

    /// <summary>The ini key every guide warns about: empty means anyone who types /AdminLogin becomes admin.</summary>
    public bool AdminPasswordIsEmpty => string.IsNullOrWhiteSpace(AdminPassword);

    partial void OnAdminPasswordChanged(string value) => OnPropertyChanged(nameof(AdminPasswordIsEmpty));

    public string SettingsIniPath => SelectedProfile is null ? "" : DedicatedServerLayout.SettingsIniPath(SelectedProfile.InstallFolder, SavedDirSuffix);

    /// <summary>The exact line Start runs, rebuilt from the form as you type.</summary>
    public string CommandLinePreview => SelectedProfile is null ? "" : DedicatedServerCommandLine.BuildDisplayLine(BuildProfileFromForm(SelectedProfile, strict: false) ?? SelectedProfile);

    partial void OnSteamServerNameChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));
    partial void OnPortTextChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));
    partial void OnQueryPortTextChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));
    partial void OnMultihomeChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));
    partial void OnLogFileNameChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));

    partial void OnSavedDirSuffixChanged(string value)
    {
        OnPropertyChanged(nameof(CommandLinePreview));
        OnPropertyChanged(nameof(SettingsIniPath));
    }

    // ---- Run state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStateText), nameof(IsStopped))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
    private RunningServer? _running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStateText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
    private bool _isBusy;

    public bool IsRunning => Running is not null;

    public bool IsStopped => Running is null;

    public string RunStateText => IsBusy && Running is null ? "STARTING"
        : IsBusy ? "STOPPING"
        : Running is { } r ? $"RUNNING · up {FormatUptime(DateTime.Now - r.StartTime)}"
        : "STOPPED";

    partial void OnRunningChanged(RunningServer? value) => OnPropertyChanged(nameof(IsRunning));

    // ---- Log ----

    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty] private bool _hideNoise = true;
    [ObservableProperty] private bool _followLog = true;
    [ObservableProperty] private string _logFilter = "";

    public string LogPath => SelectedProfile is null ? "" : DedicatedServerLayout.LogPath(SelectedProfile.InstallFolder, SelectedProfile.SavedDirSuffix, SelectedProfile.LogFileName);

    partial void OnHideNoiseChanged(bool value) => RebuildVisibleLog();
    partial void OnLogFilterChanged(string value) => RebuildVisibleLog();

    /// <summary>Raised after new lines are shown and FollowLog is on — the view scrolls to the end.</summary>
    public event EventHandler? LogAppended;

    // ---- Commands ----

    [RelayCommand]
    private void AddExistingServer()
    {
        var picked = _pickFolder("Select the dedicated server's install folder (the one with IcarusServer.exe)");
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        var root = DedicatedServerLayout.FindInstallRoot(picked);
        if (root is null)
        {
            StatusMessage = $"No IcarusServer.exe in '{picked}' or the folders above it. Pick the dedicated server's install folder (the one SteamCMD downloaded app 2089300 into).";
            return;
        }

        var existing = Profiles.FirstOrDefault(p => string.Equals(p.InstallFolder, root, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(p.SavedDirSuffix));
        if (existing is not null)
        {
            SelectedProfile = existing;
            StatusMessage = $"'{existing.Name}' already points at that server.";
            return;
        }

        var ini = ServerSettingsIni.Load(DedicatedServerLayout.SettingsIniPath(root, null)).Read();
        var profile = new DedicatedServerProfile
        {
            Name = new DirectoryInfo(root).Name,
            InstallFolder = root,
            SteamServerName = ini.SessionName ?? "Icarus Server",
        };
        _store.Save(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        var adoptedSteamCmd = AdoptInstallingSteamCmd(root);
        StatusMessage = (File.Exists(SettingsIniPath)
            ? $"Added '{profile.Name}' — settings read from its ServerSettings.ini."
            : $"Added '{profile.Name}'. It has no ServerSettings.ini yet; Save (or Start) writes one, so the first-launch bug where the file never appears doesn't apply.")
            + (adoptedSteamCmd is null ? "" : $" Using the SteamCMD that installed it: {adoptedSteamCmd}.");
        _activityLog.Log($"Added dedicated server '{profile.Name}' ({root}).", ActivityEntryKind.Info);
    }

    private bool CanUseProfile() => SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanUseProfile))]
    private void RemoveProfile()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        if (!_dialogService.Confirm(
                $"Remove '{profile.Name}' from this list?\n\nOnly the entry here goes away — the server's files, worlds and settings in '{profile.InstallFolder}' are not touched.",
                "Remove server", ThemedConfirmSeverity.Question))
        {
            return;
        }

        _store.Delete(profile.Id);
        Profiles.Remove(profile);
        SelectedProfile = Profiles.FirstOrDefault();
        StatusMessage = $"Removed '{profile.Name}' from the list.";
    }

    [RelayCommand(CanExecute = nameof(CanUseProfile))]
    private void SaveSettings() => TrySave();

    private bool CanStart() => SelectedProfile is not null && Running is null && !IsBusy && !IsSteamCmdBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        // TrySave replaces the selected profile with one built from the form, so read it AFTER
        // saving — launching the pre-save object ran the server with stale options.
        if (SelectedProfile is null || !TrySave() || SelectedProfile is not { } profile)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Skip what's already in the log: the new run's file replaces it (LogTailer starts over
            // when the file shrinks), so the panel shows only this run.
            _tailer?.SeekToEnd();
            _allLogLines.Clear();
            LogLines.Clear();
            var watch = GetWatch(profile);
            watch.Automation.NoteManualStart();
            _processes.Start(profile);
            watch.CleanExitSeen = false;
            if (profile.StartMinimized)
            {
                watch.MinimizeUntil = Clock() + TimeSpan.FromMinutes(2);
            }

            StatusMessage = $"Starting '{profile.Name}'…";
            _activityLog.Log($"Started dedicated server '{profile.Name}': {DedicatedServerCommandLine.BuildDisplayLine(profile)}", ActivityEntryKind.Info);

            // The launcher hands off to the real server process; give it a moment to show up.
            for (var i = 0; i < 20 && Running is null; i++)
            {
                await Task.Delay(StartPollDelay);
                Tick();
            }

            StatusMessage = Running is null
                ? $"'{profile.Name}' didn't appear as a running process. Check its console window and the log below."
                : $"'{profile.Name}' is running.";
            if (Running is null)
            {
                _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't start: {ex.Message}";
            _activityLog.Log($"Couldn't start dedicated server '{profile.Name}': {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStop() => SelectedProfile is not null && Running is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync() => await StopCoreAsync(ask: true);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task RestartAsync()
    {
        if (await StopCoreAsync(ask: true))
        {
            if (SelectedProfile is { UpdateOnRestart: true } profile)
            {
                await UpdateProfileIfNewerAsync(GetWatch(profile));
            }

            await StartAsync();
        }
    }

    private async Task<bool> StopCoreAsync(bool ask)
    {
        if (SelectedProfile is not { } profile || Running is not { } running)
        {
            return false;
        }

        if (ask && !_dialogService.Confirm(
                $"Stop '{profile.Name}'?\n\nConnected players are disconnected. The server is asked to shut down cleanly (it saves on exit); if it hasn't closed after 60 seconds it is ended.",
                "Stop server", ThemedConfirmSeverity.Warning))
        {
            return false;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Stopping '{profile.Name}'…";
            GetWatch(profile).Automation.NoteIntentionalStop(Clock());
            var clean = await _processes.StopAsync(running, TimeSpan.FromSeconds(60));
            Tick();
            StatusMessage = clean ? $"'{profile.Name}' stopped." : $"'{profile.Name}' didn't close within 60 seconds and was ended. The last autosave is intact.";
            _activityLog.Log(StatusMessage, clean ? ActivityEntryKind.Info : ActivityEntryKind.Warning);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't stop: {ex.Message}";
            _activityLog.Log($"Couldn't stop dedicated server '{profile.Name}': {ex.Message}", ActivityEntryKind.Warning);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenInstallFolder()
    {
        if (SelectedProfile is { } p && Directory.Exists(p.InstallFolder))
        {
            UrlOpener.TryOpen(p.InstallFolder);
        }
    }

    [RelayCommand]
    private void OpenSavedFolder()
    {
        if (SelectedProfile is { } p)
        {
            var folder = DedicatedServerLayout.SavedFolder(p.InstallFolder, p.SavedDirSuffix);
            StatusMessage = Directory.Exists(folder) ? null : $"'{folder}' doesn't exist yet — the server creates it on its first start.";
            if (Directory.Exists(folder))
            {
                UrlOpener.TryOpen(folder);
            }
        }
    }

    [RelayCommand]
    private void CopyCommandLine()
    {
        try
        {
            Clipboard.SetText(CommandLinePreview);
            StatusMessage = "Command line copied.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't copy: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        _allLogLines.Clear();
        LogLines.Clear();
    }

    /// <summary>One heartbeat (every second from the timer): is the server running, and what did it log since last time.</summary>
    public void Tick()
    {
        if (SelectedProfile is not { } profile)
        {
            Running = null;
            return;
        }

        try
        {
            var found = _processes.FindRunning(profile);
            if (found?.ProcessId != Running?.ProcessId)
            {
                var startedOrStopped = (found is null) != (Running is null);
                Running = found;
                if (startedOrStopped)
                {
                    // The server writes its worlds and LastProspectName as it runs and on exit.
                    RefreshProspectList(profile);
                }
            }
            else
            {
                OnPropertyChanged(nameof(RunStateText));
            }
        }
        catch (Exception)
        {
            // Process enumeration hiccup — keep the last known state.
        }

        if (_tailer is not null)
        {
            AppendLog(_tailer.Poll());
        }

        ExpireStalePlayers();
        RefreshOverview();
    }

    // ---- Internals ----

    private void LoadForm()
    {
        ValidationMessage = null;
        Prospects.Clear();
        if (SelectedProfile is not { } p)
        {
            ProfileName = SteamServerName = PortText = QueryPortText = Multihome = SavedDirSuffix = LogFileName = "";
            LoadAutomation(null);
            JoinPassword = AdminPassword = MaxPlayersText = ShutdownIfNotJoinedForText = ShutdownIfEmptyForText = GameSaveFrequencyText = LoadProspect = CreateProspect = "";
            OnPropertyChanged(nameof(InstallFolder));
            return;
        }

        ProfileName = p.Name;
        LoadAutomation(p);
        Branch = SteamCmd.NormalizeBranch(p.Branch);
        SteamServerName = p.SteamServerName;
        PortText = p.Port.ToString(CultureInfo.InvariantCulture);
        QueryPortText = p.QueryPort.ToString(CultureInfo.InvariantCulture);
        Multihome = p.Multihome;
        SavedDirSuffix = p.SavedDirSuffix;
        LogFileName = p.LogFileName;

        var s = ServerSettingsIni.Load(DedicatedServerLayout.SettingsIniPath(p.InstallFolder, p.SavedDirSuffix)).Read();
        JoinPassword = s.JoinPassword ?? "";
        AdminPassword = s.AdminPassword ?? "";
        MaxPlayersText = s.MaxPlayers?.ToString(CultureInfo.InvariantCulture) ?? "";
        ShutdownIfNotJoinedForText = FormatNumber(s.ShutdownIfNotJoinedFor);
        ShutdownIfEmptyForText = FormatNumber(s.ShutdownIfEmptyFor);
        GameSaveFrequencyText = FormatNumber(s.GameSaveFrequency);
        LoadProspect = s.LoadProspect ?? "";
        CreateProspect = s.CreateProspect ?? "";
        AllowNonAdminsToLaunchProspects = s.AllowNonAdminsToLaunchProspects;
        AllowNonAdminsToDeleteProspects = s.AllowNonAdminsToDeleteProspects;
        ResumeProspect = s.ResumeProspect;
        SaveGameOnExit = s.SaveGameOnExit;
        FiberFoliageRespawn = s.FiberFoliageRespawn;
        LargeStonesRespawn = s.LargeStonesRespawn;
        LastProspectName = s.LastProspectName;

        foreach (var name in DedicatedServerLayout.ListProspects(p.InstallFolder, p.SavedDirSuffix))
        {
            Prospects.Add(name);
        }

        WorldSettings.Reload();
        OnPropertyChanged(nameof(InstallFolder));
        OnPropertyChanged(nameof(SettingsIniPath));
        OnPropertyChanged(nameof(CommandLinePreview));
        OnPropertyChanged(nameof(LogPath));
    }

    /// <summary>
    /// Re-reads the saved worlds and LastProspectName without reloading the rest of the form (which
    /// would throw away unsaved edits). Used when the server starts or stops.
    /// </summary>
    private void RefreshProspectList(DedicatedServerProfile p)
    {
        try
        {
            LastProspectName = ServerSettingsIni.Load(DedicatedServerLayout.SettingsIniPath(p.InstallFolder, p.SavedDirSuffix)).Read().LastProspectName;
            var names = DedicatedServerLayout.ListProspects(p.InstallFolder, p.SavedDirSuffix).ToList();
            if (!names.SequenceEqual(Prospects))
            {
                Prospects.Clear();
                foreach (var name in names)
                {
                    Prospects.Add(name);
                }
            }

            // The server just wrote its worlds: show what they hold now.
            WorldSettings.Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The server may still hold the files — the next start/stop tries again.
        }
    }

    /// <summary>Validates the form; on success writes the profile and the ini. Returns false (with ValidationMessage set) when anything is out of range.</summary>
    internal bool TrySave()
    {
        if (SelectedProfile is not { } current)
        {
            return false;
        }

        var updated = BuildProfileFromForm(current, strict: true);
        var settings = updated is null ? null : BuildSettingsFromForm();
        if (updated is null || settings is null)
        {
            return false;
        }

        if (Profiles.Any(p => p.Id != updated.Id
                && string.Equals(p.InstallFolder, updated.InstallFolder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.SavedDirSuffix, updated.SavedDirSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            ValidationMessage = "Another profile already uses this install with the same Saved suffix — give this one its own suffix (e.g. _2) so their worlds and settings don't collide.";
            return false;
        }

        try
        {
            var iniPath = DedicatedServerLayout.SettingsIniPath(updated.InstallFolder, updated.SavedDirSuffix);
            var ini = ServerSettingsIni.Load(iniPath);
            ini.Write(settings);
            ini.Save(iniPath);
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Couldn't write ServerSettings.ini: {ex.Message}";
            return false;
        }

        var index = Profiles.IndexOf(current);
        _store.Save(updated);
        SyncFirewallPortsAfterSave(current, updated);

        // Swap the list entry and re-select it without reloading the form from disk (it already
        // holds what was saved) — the ListBox briefly pushes a null selection while the item is
        // replaced, which must not clear the form either.
        _suppressFormReload = true;
        try
        {
            if (index >= 0)
            {
                Profiles[index] = updated;
            }

            SelectedProfile = updated;
        }
        finally
        {
            _suppressFormReload = false;
        }

        OnPropertyChanged(nameof(LogPath));
        AttachLog();

        ValidationMessage = null;
        StatusMessage = Running is not null ? "Saved. Restart the server to apply." : "Saved.";
        return true;
    }

    private DedicatedServerProfile? BuildProfileFromForm(DedicatedServerProfile current, bool strict)
    {
        string? error = null;
        if (!TryParsePort(PortText, out var port))
        {
            error = "Game port must be a number from 1 to 65535.";
        }
        else if (!TryParsePort(QueryPortText, out var queryPort))
        {
            error = "Query port must be a number from 1 to 65535.";
        }
        else if (port == queryPort)
        {
            error = "Game port and query port must differ.";
        }
        else if (string.IsNullOrWhiteSpace(ProfileName))
        {
            error = "Give the profile a name.";
        }
        else if (SavedDirSuffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || LogFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Saved suffix and log file name can't contain \\ / : * ? \" < > |.";
        }
        else
        {
            var built = new DedicatedServerProfile
            {
                Id = current.Id,
                Name = ProfileName.Trim(),
                InstallFolder = current.InstallFolder,
                SteamServerName = DedicatedServerCommandLine.Sanitize(SteamServerName),
                Port = port,
                QueryPort = queryPort,
                Multihome = Multihome.Trim(),
                SavedDirSuffix = SavedDirSuffix.Trim(),
                LogFileName = LogFileName.Trim(),
                Branch = SteamCmd.NormalizeBranch(Branch),
            };
            CopyAutomation(current, built);
            return built;
        }

        if (strict)
        {
            ValidationMessage = error;
        }

        return null;
    }

    private DedicatedServerSettings? BuildSettingsFromForm()
    {
        int? maxPlayers = null;
        if (MaxPlayersText.Trim().Length > 0)
        {
            if (!int.TryParse(MaxPlayersText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp)
                || mp < DedicatedServerDefaults.MinPlayers || mp > DedicatedServerDefaults.MaxPlayers)
            {
                ValidationMessage = $"Max players must be {DedicatedServerDefaults.MinPlayers} to {DedicatedServerDefaults.MaxPlayers} (or blank for the game default).";
                return null;
            }

            maxPlayers = mp;
        }

        if (!TryParseOptionalNumber(ShutdownIfNotJoinedForText, out var notJoined)
            || !TryParseOptionalNumber(ShutdownIfEmptyForText, out var empty))
        {
            ValidationMessage = "Shutdown timers are seconds: a number, -1 for never, or blank for the game default.";
            return null;
        }

        if (!TryParseOptionalNumber(GameSaveFrequencyText, out var saveFrequency) || saveFrequency <= 0)
        {
            ValidationMessage = "Autosave interval is minutes: a positive number, or blank for the game default.";
            return null;
        }

        return new DedicatedServerSettings
        {
            SessionName = DedicatedServerCommandLine.Sanitize(SteamServerName) is { Length: > 0 } n ? n : null,
            JoinPassword = Blank(JoinPassword),
            AdminPassword = Blank(AdminPassword),
            MaxPlayers = maxPlayers,
            ShutdownIfNotJoinedFor = notJoined,
            ShutdownIfEmptyFor = empty,
            GameSaveFrequency = saveFrequency,
            LoadProspect = Blank(LoadProspect),
            CreateProspect = Blank(CreateProspect),
            AllowNonAdminsToLaunchProspects = AllowNonAdminsToLaunchProspects,
            AllowNonAdminsToDeleteProspects = AllowNonAdminsToDeleteProspects,
            ResumeProspect = ResumeProspect,
            SaveGameOnExit = SaveGameOnExit,
            FiberFoliageRespawn = FiberFoliageRespawn,
            LargeStonesRespawn = LargeStonesRespawn,
        };
    }

    private void AttachLog()
    {
        _allLogLines.Clear();
        LogLines.Clear();
        _tailer = SelectedProfile is null ? null : new LogTailer(LogPath);
        ResetPlayersAndEvents();
        if (_tailer is not null)
        {
            // Read the whole current run so the player list is right (someone who joined hours ago
            // is still online), but only put its newest lines in the panel.
            var lines = _tailer.Poll(maxBytes: 64 << 20).Select(ServerLogFilter.Redact).ToList();
            FeedPlayersAndEvents(lines);
            AppendLog(lines.Count > MaxLogLines ? lines.GetRange(lines.Count - MaxLogLines, MaxLogLines) : lines, feedEvents: false);
        }
    }

    private void AppendLog(IReadOnlyList<string> lines, bool feedEvents = true)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // The server logs the join password in plain text on every login — never show or keep it.
        lines = [.. lines.Select(ServerLogFilter.Redact)];
        if (feedEvents)
        {
            FeedPlayersAndEvents(lines);
        }
        _allLogLines.AddRange(lines);
        if (_allLogLines.Count > MaxLogLines)
        {
            _allLogLines.RemoveRange(0, _allLogLines.Count - MaxLogLines);
        }

        var added = false;
        foreach (var line in lines.Where(IsVisible))
        {
            LogLines.Add(line);
            added = true;
        }

        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }

        if (added && FollowLog)
        {
            LogAppended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RebuildVisibleLog()
    {
        LogLines.Clear();
        foreach (var line in _allLogLines.Where(IsVisible))
        {
            LogLines.Add(line);
        }
    }

    private bool IsVisible(string line) =>
        line.Length > 0
        && (!HideNoise || !ServerLogFilter.IsNoise(line))
        && (string.IsNullOrWhiteSpace(LogFilter) || line.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? PickSteamCmdWithDialog()
    {
        var dialog = new OpenFileDialog { Title = "Select your steamcmd.exe", Filter = "steamcmd.exe|steamcmd.exe|Programs (*.exe)|*.exe" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PickFolderWithDialog(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    private static bool TryParseOptionalNumber(string text, out double? value)
    {
        value = null;
        if (text.Trim().Length == 0)
        {
            return true;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            value = v;
            return true;
        }

        return false;
    }

    private static string FormatNumber(double? value) =>
        value is null ? "" : value.Value.ToString(value.Value % 1 == 0 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string FormatUptime(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s" : $"{Math.Max(0, span.Seconds)}s";
}
