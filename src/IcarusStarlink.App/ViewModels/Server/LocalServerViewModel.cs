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

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Server page's "Local server" tab: dedicated servers on THIS PC:
/// adopt an existing install, edit its launch options and ServerSettings.ini, start / stop /
/// restart it, and follow its log live. (Installing and updating through SteamCMD is in the
/// .SteamCmd partial.)
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
    private readonly IHostNotifier _hostNotifier;
    private readonly List<string> _allLogLines = [];

    public LocalServerViewModel(
        IDedicatedServerProfileStore store, IDedicatedServerProcessService processes, IDialogService dialogService,
        IActivityLog activityLog, ISteamCmdService? steamCmd = null, Core.Settings.ISettingsService? settings = null,
        string? mergedPakPath = null, string? backupsRoot = null, Func<string, string?>? pickFolder = null, Func<string?>? pickSteamCmdExe = null, bool startTimer = true,
        IFirewallService? firewall = null, string? dataFolder = null,
        Core.Library.ILibraryRepository? library = null, PakIO.Patches.IPatchService? patchService = null, Core.Ue4ss.IUe4ssModMetaStore? ue4ssMetaStore = null,
        IPatchImporter? patchImporter = null, INetworkAddressService? network = null, IHostNotifier? hostNotifier = null)
    {
        _network = network;
        _hostNotifier = hostNotifier ?? NullHostNotifier.Instance;
        // Set before SelectedProfile below: selecting a profile loads its CreateProspect, which
        // reads (and caches) ProspectChoices from this folder. As an init property it arrived
        // too late, so the dropdown only ever held "Don't create one".
        DataFolder = dataFolder;
        // Share with players (the Mods tab): optional, the page works without them.
        _library = library;
        _patchService = patchService;
        _ue4ssMetaStore = ue4ssMetaStore;
        _patchImporter = patchImporter;
        _firewall = firewall;
        _steamCmd = steamCmd;
        _settings = settings;
        _mergedPakPath = mergedPakPath;
        _backupsRoot = backupsRoot ?? Path.Combine(Path.GetTempPath(), "IcarusStarlink", "ServerBackups");
        _store = store;
        _processes = processes;
        _dialogService = dialogService;
        _activityLog = activityLog;
        _pickFolder = pickFolder ?? (title => dialogService.PickFolder(title));
        _pickSteamCmdExe = pickSteamCmdExe ?? (() => dialogService.PickFile("Select your steamcmd.exe", "steamcmd.exe|steamcmd.exe|Programs (*.exe)|*.exe"));
        WorldSettings = new WorldSettingsViewModel(
            () => SelectedProfile is { } p ? DedicatedServerLayout.ProspectsFolder(p.InstallFolder, p.SavedDirSuffix) : null,
            () => DataFolder,
            () => IsRunning ? "Stop the server first: it rewrites its worlds when it shuts down, which would undo this."
                : IsBusy || IsAutomationBusy ? "Wait until the server has finished starting, stopping or updating." : null,
            Path.Combine(_backupsRoot, "WorldSettings"),
            activityLog,
            dialogService,
            // Every server's world backups share this folder: the profile id keeps them apart.
            // Restore is refused by the same rule as Save (server running, starting, stopping, updating).
            backupOwner: () => SelectedProfile is { } p ? IcarusStarlink.PakIO.Worlds.WorldBackups.ServerOwner(p.Id) : null,
            backupsKept: () => Core.Settings.BackupLimits.SaveKept(settings))
        {
            GreyOutWhenBlocked = true,
        };

        foreach (var profile in store.GetAll())
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault();
        // Closing the window asks this before keeping Starlink in the tray.
        _hostNotifier.WatchServers(() => ServersNeedStarlink);
        Profiles.CollectionChanged += (_, _) =>
        {
            RaiseSteamCmdChanged();
            RaiseServerFilesChanged();
        };
        if (store.LoadProblem is { } problem)
        {
            StatusMessage = problem;
            activityLog.Log(problem, ActivityEntryKind.Warning);
        }

        // Refresh game data re-extracts D_ProspectList — rebuild the prospect choices from it.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((LocalServerViewModel)recipient).ReloadProspectChoices());

        // An app update must not close Starlink in the middle of server work.
        WeakReferenceMessenger.Default.Register<BackgroundWorkRequestMessage>(this, (recipient, message) =>
        {
            foreach (var work in ((LocalServerViewModel)recipient).DescribeWorkInProgress())
            {
                message.Reply(work);
            }
        });

        // A new merged pak was built — "Install my merged pak" and its summary follow it.
        WeakReferenceMessenger.Default.Register<MergedPakBuiltMessage>(this, (recipient, _) => ((LocalServerViewModel)recipient).RefreshMergedPak());

        // A UE4SS mod added, updated or removed in the Library (or the loader changed): the server
        // table compares against the game's copy, so it's read again.
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((LocalServerViewModel)recipient).RefreshMods());
        WeakReferenceMessenger.Default.Register<Ue4ssLoaderChangedMessage>(this, (recipient, _) => ((LocalServerViewModel)recipient).RefreshMods());

        if (startTimer && Application.Current is not null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Normal, System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                _ = TickAsync(skipIfBusy: true);
                AutomationTick();
            };
            _timer.Start();
            _ = StartServersMarkedForStartupAsync();
            _ = RefreshLanAddressAsync();
        }
    }

    /// <summary>
    /// Tests run the file and process work inline so every step is deterministic; the app runs it on
    /// the thread pool so a 9 MB log, a world zip or a process scan never stalls the window.
    /// </summary>
    internal bool RunWorkInline { get; set; }

    private Task<T> OffUiThread<T>(Func<T> work) => RunWorkInline ? Task.FromResult(work()) : Task.Run(work);

    private Task OffUiThread(Action work)
    {
        if (!RunWorkInline)
        {
            return Task.Run(work);
        }

        work();
        return Task.CompletedTask;
    }

    public ObservableCollection<DedicatedServerProfile> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile), nameof(HasNoProfile))]
    // Check for update, Update and the two firewall buttons need a server too: without them here,
    // adding the first server left them greyed out.
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(SaveSettingsCommand), nameof(RemoveProfileCommand),
        nameof(CheckForUpdateCommand), nameof(UpdateServerCommand), nameof(VerifyServerFilesCommand), nameof(OpenFirewallPortsCommand), nameof(RemoveFirewallRulesCommand))]
    private DedicatedServerProfile? _selectedProfile;

    public bool HasProfile => SelectedProfile is not null;

    public bool HasNoProfile => SelectedProfile is null;

    private bool _suppressFormReload;

    partial void OnSelectedProfileChanged(DedicatedServerProfile? value)
    {
        RaiseServerFilesChanged();
        // Its ports change with a Settings save too (which swaps the profile without reloading the form).
        RaiseConnectChanged();
        if (_suppressFormReload)
        {
            return;
        }

        // What automation last saw for this server, straight away: until the next process scan,
        // Running used to stay the PREVIOUS server's, so Stop or Update in that second acted on it.
        Running = value is not null && _watches.TryGetValue(value.Id, out var watch) ? watch.Running : null;
        // The boxes still hold the last server's lists here; reloading them drops what wasn't saved.
        var unsavedLists = UnsavedListNames();
        LoadForm();
        AttachLog();
        Tick();
        RefreshServerExtras();
        LoadFirewall();
        RefreshOverview();
        if (unsavedLists is not null)
        {
            StatusMessage = $"Changes to the last server's {unsavedLists} weren't saved (Save lists wasn't clicked). Pick that server again and redo them.";
        }
    }

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _validationMessage;

    // ---- Run state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStateText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
    private RunningServer? _running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStateText), nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(UpdateServerCommand), nameof(VerifyServerFilesCommand),
        nameof(CheckForUpdateCommand), nameof(InstallNewServerCommand), nameof(InstallSteamCmdCommand),
        nameof(AddExistingServerCommand), nameof(RemoveProfileCommand))]
    private bool _isBusy;

    public bool IsRunning => Running is not null;

    /// <summary>The server list is locked while one is starting, stopping or updating: Start/Stop/Restart/Update act on the selected server, so switching mid-way would act on another one.</summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>What the server is busy with when it's neither starting nor stopping ("UPDATING"); set before IsBusy, which refreshes the state text.</summary>
    private string? _busyWith;

    public string RunStateText => IsAutomationBusy ? "AUTOMATIC RESTART"
        : IsBusy && _busyWith is { } busyWith ? busyWith
        : IsBusy && Running is null ? "STARTING"
        : IsBusy ? "STOPPING"
        : Running is { } r ? $"RUNNING · up {FormatUptime(Clock() - r.StartTime)}"
        : "STOPPED";

    partial void OnRunningChanged(RunningServer? value)
    {
        OnPropertyChanged(nameof(IsRunning));
        RaiseServerFilesChanged();
    }

    // ---- Commands ----

    /// <summary>Adding a server selects it, so it waits while the list is locked (IsNotBusy): a start in progress used to read the new selection and report "didn't appear".</summary>
    private bool CanAddExistingServer() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAddExistingServer))]
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

        // A server the owner's own script runs from here: with its options copied, Starlink sees it
        // running and edits its world and settings, not the plain Saved folder's.
        var adopted = OfferRunningServerOptions(root);
        var suffix = adopted?.SavedDirSuffix ?? "";
        var existing = Profiles.FirstOrDefault(p => SameInstall(p.InstallFolder, root) && string.Equals((p.SavedDirSuffix ?? "").Trim(), suffix, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedProfile = existing;
            StatusMessage = $"'{existing.Name}' already points at that server.";
            return;
        }

        DedicatedServerSettings ini;
        try
        {
            ini = ServerSettingsIni.Load(DedicatedServerLayout.SettingsIniPath(root, suffix)).Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Couldn't read that server's ServerSettings.ini: {ex.Message}";
            return;
        }

        var profile = new DedicatedServerProfile
        {
            Name = new DirectoryInfo(root).Name,
            InstallFolder = root,
            SteamServerName = ini.SessionName ?? "Icarus Server",
            // Settings › Maintenance › Backups to keep › Server backups: what a new server starts with.
            BackupsToKeep = Core.Settings.BackupLimits.ServerKept(_settings),
            SavedDirSuffix = suffix,
            LogFileName = adopted?.LogFileName ?? "",
            Port = adopted?.Port ?? DedicatedServerDefaults.GamePort,
            QueryPort = adopted?.QueryPort ?? DedicatedServerDefaults.QueryPort,
        };
        SaveToStore(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        var adoptedSteamCmd = AdoptInstallingSteamCmd(root);
        StatusMessage = (File.Exists(SettingsIniPath)
            ? $"Added '{profile.Name}' — settings read from its ServerSettings.ini."
            : $"Added '{profile.Name}'. It has no ServerSettings.ini yet; Save (or Start) writes one, so the first-launch bug where the file never appears doesn't apply.")
            + (adopted is null ? "" : " Its Saved folder, log file and ports were copied from the running server.")
            + (adoptedSteamCmd is null ? "" : $" Using the SteamCMD that installed it: {adoptedSteamCmd}.");
        _activityLog.Log($"Added dedicated server '{profile.Name}' ({root}).", ActivityEntryKind.Info);
    }

    /// <summary>
    /// A server runs from this install that no server in the list accounts for, and its command
    /// line sets something the defaults don't (Saved suffix, log file, ports): asks whether the new
    /// server takes them. Null when there is nothing to copy or the owner says No.
    /// </summary>
    private DedicatedServerLaunchOptions? OfferRunningServerOptions(string installFolder)
    {
        static bool Usable(string? name) => name is null || name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        var options = UnknownServersFrom(installFolder, KnownSavedSuffixes(installFolder))
            .OrderBy(s => s.IsLauncher)
            .Select(s => DedicatedServerCommandLine.ReadLaunchOptions(s.CommandLine!))
            .FirstOrDefault(o => o.DiffersFromDefaults && Usable(o.SavedDirSuffix) && Usable(o.LogFileName));
        if (options is null)
        {
            return null;
        }

        return _dialogService.Confirm(
                "A server is running from this install with its own launch options:\n\n"
                + $"  Saved folder: Saved{options.SavedDirSuffix}\n"
                + $"  Log file: {options.LogFileName ?? DedicatedServerDefaults.DefaultLogFileName}\n"
                + $"  Game port: {options.Port ?? DedicatedServerDefaults.GamePort}, query port: {options.QueryPort ?? DedicatedServerDefaults.QueryPort}\n\n"
                + "Set up the new server with these? Then Starlink sees it running, reads its players, and edits that world's settings. Choose No to use the defaults.",
                "Add existing server", ThemedConfirmSeverity.Question)
            ? options
            : null;
    }

    private bool CanUseProfile() => SelectedProfile is not null;

    /// <summary>What closing the app now would cut off (asked before an app update).</summary>
    internal IEnumerable<string> DescribeWorkInProgress()
    {
        if (IsSteamCmdBusy)
        {
            yield return "SteamCMD (installing, updating or checking a server)";
        }

        if (AnyAutomationBusy)
        {
            yield return "the automatic server restart or update";
        }

        if (IsBusy)
        {
            yield return "the server starting, stopping or updating";
        }

        if (IsBackupBusy)
        {
            yield return "the server backup or restore";
        }
    }

    /// <summary>Writes the profile list; when the store refuses (its file couldn't be read at startup) or the write fails, the status line says so.</summary>
    private bool SaveToStore(DedicatedServerProfile profile)
    {
        if (_store.Save(profile))
        {
            return true;
        }

        StatusMessage = _store.LoadProblem ?? "Couldn't save the server list. Your change applies until Starlink closes; check the log for why.";
        return false;
    }

    /// <summary>
    /// Not while the server starts, stops or updates, nor while automation restarts it: removing
    /// it then dropped its watch, and the restart carried on and launched a server nothing watched.
    /// </summary>
    private bool CanRemoveProfile() => SelectedProfile is not null && !IsBusy && !IsAutomationBusy;

    /// <summary>
    /// Removes the list entry only. Starlink's own firewall rules for it are offered for removal
    /// in a second question: once the profile is gone nothing in Starlink could find them again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveProfile))]
    private async Task RemoveProfileAsync()
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

        var rules = await ReadOwnFirewallRulesAsync(profile);
        var removeRules = rules.Count > 0 && _dialogService.Confirm(
            $"Also remove the {rules.Count} firewall rule(s) Starlink added for '{profile.Name}'?\n\n{string.Join("\n", rules.Select(r => "  " + r))}\n\nRules you made yourself are not touched. Windows asks for administrator approval once. Choose No to keep them (you can delete them later in Windows Defender Firewall).",
            "Remove firewall rules too", ThemedConfirmSeverity.Question);

        var deleted = _store.Delete(profile.Id);
        Profiles.Remove(profile);
        _watches.Remove(profile.Id);
        SelectedProfile = Profiles.FirstOrDefault();
        var message = $"Removed '{profile.Name}' from the list.";
        if (removeRules)
        {
            message += await RemoveOwnFirewallRulesAsync(profile, rules)
                ? $" Its {rules.Count} firewall rule(s) were removed too."
                : " Its firewall rules couldn't be removed; delete them in Windows Defender Firewall (wf.msc) if you no longer need them.";
        }
        else if (rules.Count > 0)
        {
            message += $" Its {rules.Count} firewall rule(s) were kept.";
        }

        StatusMessage = deleted
            ? message
            : $"{message} {_store.LoadProblem ?? "The server list couldn't be saved, so it is back in the list the next time Starlink opens."}";
    }

    [RelayCommand(CanExecute = nameof(CanUseProfile))]
    private void SaveSettings() => TrySave();

    private bool CanStart() => SelectedProfile is not null && Running is null && !IsBusy && !IsSteamCmdBusy && !IsAutomationBusy && !IsBackupBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        // TrySave replaces the selected profile with one built from the form, so read it AFTER
        // saving — launching the pre-save object ran the server with stale options.
        if (SelectedProfile is null || !TrySave() || SelectedProfile is not { } profile)
        {
            return;
        }

        // The list is read here, on the UI thread: it isn't locked until StartCoreAsync sets IsBusy.
        var known = KnownSavedSuffixes(profile.InstallFolder);
        if (await OffUiThread(() => UnknownServersFrom(profile.InstallFolder, known)) is { Count: > 0 } unknown
            && !_dialogService.Confirm(
                $"Another Icarus server is already running from this install ({DescribeSavedFolders(unknown)}), and no server in this list is set up for it. "
                + $"Starting '{profile.Name}' too runs two servers from the same files: each needs its own game and query port, or players can't join one of them.\n\n"
                + "To manage the running one here instead, click No, then Add existing server… while it still runs: its Saved folder, log file and ports are copied.\n\n"
                + $"Start '{profile.Name}' anyway?",
                "Another server is running", ThemedConfirmSeverity.Warning))
        {
            StatusMessage = $"'{profile.Name}' wasn't started.";
            return;
        }

        await StartCoreAsync(profile);
    }

    /// <summary>
    /// Servers running from this install that no server in the list accounts for: their Saved
    /// suffix matches none of the profiles on this install (one the owner's own script started).
    /// A process whose command line can't be read is left out, since it can't be told apart.
    /// Touches no view-model state, so it may run off the UI thread.
    /// </summary>
    private IReadOnlyList<ServerProcess> UnknownServersFrom(string installFolder, IReadOnlySet<string> known)
    {
        try
        {
            return _processes.FindAllFromInstall(installFolder)
                .Where(s => s.CommandLine is { } line && !known.Contains(DedicatedServerCommandLine.ReadSavedDirSuffix(line)))
                .ToList();
        }
        catch (Exception)
        {
            return []; // couldn't look: a process scan hiccup, never a reason to block a start
        }
    }

    /// <summary>The Saved suffixes of the listed servers on this install. Reads Profiles, so call it on the UI thread.</summary>
    private HashSet<string> KnownSavedSuffixes(string installFolder) =>
        Profiles.Where(p => SameInstall(p.InstallFolder, installFolder))
            .Select(p => (p.SavedDirSuffix ?? "").Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool SameInstall(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>"it saves to Saved_live" (each Saved folder once: the launcher and the server it starts carry the same options).</summary>
    private static string DescribeSavedFolders(IEnumerable<ServerProcess> servers)
    {
        var folders = servers.Select(s => "Saved" + DedicatedServerCommandLine.ReadSavedDirSuffix(s.CommandLine ?? ""))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return $"it saves to {string.Join(" and ", folders)}";
    }

    /// <summary>Launches this profile (already saved) and waits for its process to show up.</summary>
    private async Task StartCoreAsync(DedicatedServerProfile profile)
    {
        IsBusy = true;
        try
        {
            // Skip what's already in the log: the new run's file replaces it (LogTailer starts over
            // when the file is replaced), so the panel shows only this run.
            var watch = GetWatch(profile);
            await StartLogAfreshAsync(watch);
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
                await TickAsync();
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

    private bool CanStop() => SelectedProfile is not null && Running is not null && !IsBusy && !IsAutomationBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (SelectedProfile is { } profile)
        {
            await StopCoreAsync(profile, ask: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task RestartAsync()
    {
        // The server being restarted, from the first click to the start: an update on restart takes
        // minutes, and the start at the end used to launch whichever server was selected by then.
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        // The form is checked before anything is stopped: a value it can't save used to show up
        // only after the stop, and the server (stopped by the owner, so never restarted) stayed down.
        if (_iniUnreadable)
        {
            StatusMessage = $"'{profile.Name}' wasn't restarted: its ServerSettings.ini couldn't be read. Click Save settings first.";
            return;
        }

        if (!ValidateFormForSave(profile, out var updated, out _))
        {
            StatusMessage = $"'{profile.Name}' wasn't restarted: fix the settings first ({ValidationMessage}). It is still running.";
            return;
        }

        // The port question too: asked by the save after the stop, a No left the server down.
        var portClash = DescribePortClash(profile, updated);
        if (portClash is not null && !ConfirmPortClash(portClash))
        {
            ValidationMessage = PortClashNotSaved;
            StatusMessage = $"'{profile.Name}' wasn't restarted: pick a game port and query port no other server in the list uses, or restart again and choose Yes. It is still running.";
            return;
        }

        if (!await StopCoreAsync(profile, ask: true))
        {
            return;
        }

        // Never start a different server than the one that was stopped.
        if (SelectedProfile?.Id != profile.Id)
        {
            StatusMessage += " Another server was selected meanwhile, so it wasn't started again; select it and click Start.";
            return;
        }

        if (profile.UpdateOnRestart)
        {
            // Busy for the whole update, so the server list stays locked on this server.
            _busyWith = "UPDATING";
            IsBusy = true;
            try
            {
                await UpdateProfileIfNewerAsync(GetWatch(profile));
            }
            finally
            {
                IsBusy = false;
                _busyWith = null;
                OnPropertyChanged(nameof(RunStateText));
            }
        }

        if (SelectedProfile?.Id != profile.Id)
        {
            return;
        }

        if (!TrySave(acceptedPortClash: portClash) || SelectedProfile is not { } saved)
        {
            // Stopped, but the save before the start failed (the ini question was cancelled, or the
            // write failed): say so, or the status line read as if the restart had worked.
            StatusMessage += $" It wasn't started again: {ValidationMessage ?? "its settings couldn't be saved."} Fix that, then click Start.";
            return;
        }

        await StartCoreAsync(saved);
    }

    /// <summary>
    /// Stops this profile's server. What is stopped is looked up again right before acting (not the
    /// Running the page last showed, which can be a few seconds old or another server's), and the
    /// process service checks it is still that same process.
    /// </summary>
    private async Task<bool> StopCoreAsync(DedicatedServerProfile profile, bool ask)
    {
        if (Running is null)
        {
            return false;
        }

        if (ask && !_dialogService.Confirm(
                $"Stop '{profile.Name}'?\n\nConnected players are disconnected. The server is asked to shut down cleanly; if it hasn't closed after 60 seconds it is ended.\n\n{ServerShutdownNotes.DescribeSaving(ReadIniSettings(profile))}",
                "Stop server", ThemedConfirmSeverity.Warning))
        {
            return false;
        }

        IsBusy = true;
        try
        {
            if (await OffUiThread(() => _processes.FindRunning(profile)) is not { } running)
            {
                await TickAsync();
                StatusMessage = $"'{profile.Name}' isn't running any more.";
                return true;
            }

            StatusMessage = $"Stopping '{profile.Name}'…";
            GetWatch(profile).Automation.NoteOwnerStop(running.ProcessId);
            bool clean;
            try
            {
                clean = await _processes.StopAsync(running, TimeSpan.FromSeconds(60));
            }
            catch
            {
                // It still runs: a crash of it later is a crash, not this stop.
                GetWatch(profile).Automation.CancelOwnerStop(running.ProcessId);
                throw;
            }

            await TickAsync();
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

    /// <summary>
    /// Every server in the list that runs from this profile's install folder, itself included. Two
    /// profiles can share one install (each with its own Saved suffix): its paks, UE4SS and the
    /// server build are shared, so changing them needs every one of them stopped, not just the
    /// selected one.
    /// </summary>
    internal IReadOnlyList<DedicatedServerProfile> RunningSiblings(DedicatedServerProfile profile)
    {
        var running = new List<DedicatedServerProfile>();
        foreach (var other in Profiles.Where(p => string.Equals(
                     Path.TrimEndingDirectorySeparator(p.InstallFolder), Path.TrimEndingDirectorySeparator(profile.InstallFolder), StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try
            {
                if (_processes.FindRunning(other) is not null)
                {
                    running.Add(other);
                }
            }
            catch (Exception)
            {
                // Couldn't look: a process scan hiccup. The selected server's own state still applies.
            }
        }

        return running;
    }

    /// <summary>"Stop 'A' and 'B' first: they run from the same install." for a refused install-wide change; null when nothing else runs from it.</summary>
    private static string? DescribeRunningSiblings(DedicatedServerProfile profile, IReadOnlyList<DedicatedServerProfile> running, string action)
    {
        var others = running.Where(p => p.Id != profile.Id).ToList();
        if (others.Count == 0)
        {
            return null;
        }

        var names = string.Join(" and ", others.Select(p => $"'{p.Name}'"));
        return $"Stop {names} before you {action}: {(others.Count == 1 ? "it runs" : "they run")} from the same install as '{profile.Name}'.";
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
    private void CopyCommandLine() => CopyText(CommandLinePreview, "Command line copied.");

    /// <summary>Puts text on the clipboard. Tests swap it so they never touch the real clipboard.</summary>
    internal Action<string> CopyToClipboard { get; set; } = Clipboard.SetText;

    /// <summary>Copies the text and says so in the status line (or why it couldn't: another program can hold the clipboard).</summary>
    private void CopyText(string text, string done)
    {
        try
        {
            CopyToClipboard(text);
            StatusMessage = done;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't copy: {ex.Message}";
        }
    }

    // ---- Internals ----

    private static string FormatUptime(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s" : $"{Math.Max(0, span.Seconds)}s";
}
