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
        _pickFolder = pickFolder ?? (title => dialogService.PickFolder(title));
        _pickSteamCmdExe = pickSteamCmdExe ?? (() => dialogService.PickFile("Select your steamcmd.exe", "steamcmd.exe|steamcmd.exe|Programs (*.exe)|*.exe"));
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
        Profiles.CollectionChanged += (_, _) => RaiseSteamCmdChanged();
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

    // ---- Run state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStateText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
    private RunningServer? _running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStateText), nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(UpdateServerCommand),
        nameof(CheckForUpdateCommand), nameof(InstallNewServerCommand), nameof(InstallSteamCmdCommand))]
    private bool _isBusy;

    public bool IsRunning => Running is not null;

    /// <summary>The server list is locked while one is starting or stopping: Start/Stop/Restart act on the selected server, so switching mid-way would act on another one.</summary>
    public bool IsNotBusy => !IsBusy;

    public string RunStateText => IsAutomationBusy ? "AUTOMATIC RESTART"
        : IsBusy && Running is null ? "STARTING"
        : IsBusy ? "STOPPING"
        : Running is { } r ? $"RUNNING · up {FormatUptime(DateTime.Now - r.StartTime)}"
        : "STOPPED";

    partial void OnRunningChanged(RunningServer? value) => OnPropertyChanged(nameof(IsRunning));

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

        DedicatedServerSettings ini;
        try
        {
            ini = ServerSettingsIni.Load(DedicatedServerLayout.SettingsIniPath(root, null)).Read();
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
        };
        SaveToStore(profile);
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
            yield return "the server starting or stopping";
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
    /// Removes the list entry only. Starlink's own firewall rules for it are offered for removal
    /// in a second question: once the profile is gone nothing in Starlink could find them again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseProfile))]
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

    private bool CanStart() => SelectedProfile is not null && Running is null && !IsBusy && !IsSteamCmdBusy && !IsAutomationBusy;

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
    private async Task StopAsync() => await StopCoreAsync(ask: true);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task RestartAsync()
    {
        var restarting = SelectedProfile?.Id;
        if (await StopCoreAsync(ask: true))
        {
            // Never start a different server than the one that was stopped.
            if (SelectedProfile?.Id != restarting)
            {
                StatusMessage += " Another server was selected meanwhile, so it wasn't started again; select it and click Start.";
                return;
            }

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
                $"Stop '{profile.Name}'?\n\nConnected players are disconnected. The server is asked to shut down cleanly; if it hasn't closed after 60 seconds it is ended.\n\n{ServerShutdownNotes.DescribeSaving(ReadIniSettings(profile))}",
                "Stop server", ThemedConfirmSeverity.Warning))
        {
            return false;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Stopping '{profile.Name}'…";
            GetWatch(profile).Automation.NoteOwnerStop(running.ProcessId);
            var clean = await _processes.StopAsync(running, TimeSpan.FromSeconds(60));
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

    // ---- Internals ----

    private static string FormatUptime(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s" : $"{Math.Max(0, span.Seconds)}s";
}
