using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 4: the Overview tab and automation — crash restarts, the daily restart, update on
/// restart, hourly auto-update, start with Starlink and start minimized. Automation runs for EVERY
/// server in the list, not just the selected one, so each gets its own watch: its own log tail
/// (who is online, did the last run exit cleanly) and its own ServerAutomation state. The rules
/// themselves live in Core's ServerAutomation; this file only observes and carries out.
/// </summary>
public sealed partial class LocalServerViewModel
{
    /// <summary>Automation looks every 5 seconds; the once-a-second tick stays cheap.</summary>
    private const int AutomationEveryTicks = 5;

    /// <summary>A watch reads the whole current log once, so players who joined hours ago are known before anything restarts the server.</summary>
    private const int WatchFirstReadBytes = 64 << 20;

    private readonly Dictionary<Guid, ServerWatch> _watches = [];
    private int _tickCount;
    private bool _automationRunning;
    private bool _loadingAutomation;

    /// <summary>Local time, swappable so tests can walk through a day.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>Gap between "is it up yet" checks after a start. Tests set it to zero.</summary>
    internal TimeSpan StartPollDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    private sealed class ServerWatch(DedicatedServerProfile profile)
    {
        public DedicatedServerProfile Profile { get; set; } = profile;
        public LogTailer? Tailer { get; set; }
        public string? TailerPath { get; set; }
        public ServerLogEvents Events { get; } = new();
        public ServerAutomation Automation { get; } = new();
        public bool CleanExitSeen { get; set; }
        public RunningServer? Running { get; set; }
        public int MissingBeats { get; set; }
        public DateTime? MinimizeUntil { get; set; }
        public string? LastAutomationEvent { get; set; }
    }

    // ---- Automation settings (saved straight into the profile, no Save button) ----

    [ObservableProperty] private bool _autoRestartOnCrash;
    [ObservableProperty] private bool _startWithApp;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _dailyRestartEnabled;
    [ObservableProperty] private string _dailyRestartTime = "05:00";
    [ObservableProperty] private bool _updateOnRestart;
    [ObservableProperty] private bool _autoUpdate;

    [ObservableProperty] private string? _dailyRestartTimeError;

    partial void OnAutoRestartOnCrashChanged(bool value) => SaveAutomation(p => p.AutoRestartOnCrash = value);
    partial void OnStartWithAppChanged(bool value) => SaveAutomation(p => p.StartWithApp = value);
    partial void OnStartMinimizedChanged(bool value) => SaveAutomation(p => p.StartMinimized = value);
    partial void OnDailyRestartEnabledChanged(bool value) => SaveAutomation(p => p.DailyRestartEnabled = value);
    partial void OnUpdateOnRestartChanged(bool value) => SaveAutomation(p => p.UpdateOnRestart = value);
    partial void OnAutoUpdateChanged(bool value) => SaveAutomation(p => p.AutoUpdate = value);

    partial void OnDailyRestartTimeChanged(string value)
    {
        if (_loadingAutomation)
        {
            return;
        }

        if (!ServerAutomation.TryParseDailyTime(value, out var time))
        {
            DailyRestartTimeError = "Use 24-hour time, like 05:00.";
            return;
        }

        DailyRestartTimeError = null;
        SaveAutomation(p => p.DailyRestartTime = time.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    private void SaveAutomation(Action<DedicatedServerProfile> apply)
    {
        if (_loadingAutomation || SelectedProfile is not { } profile)
        {
            return;
        }

        apply(profile);
        SaveToStore(profile);
        RefreshOverview();
    }

    private void LoadAutomation(DedicatedServerProfile? p)
    {
        _loadingAutomation = true;
        try
        {
            AutoRestartOnCrash = p?.AutoRestartOnCrash ?? false;
            StartWithApp = p?.StartWithApp ?? false;
            StartMinimized = p?.StartMinimized ?? false;
            DailyRestartEnabled = p?.DailyRestartEnabled ?? false;
            DailyRestartTime = p?.DailyRestartTime ?? "05:00";
            UpdateOnRestart = p?.UpdateOnRestart ?? false;
            AutoUpdate = p?.AutoUpdate ?? false;
            DailyRestartTimeError = null;
        }
        finally
        {
            _loadingAutomation = false;
        }
    }

    /// <summary>Profile fields the Settings form doesn't own, carried over when Save rebuilds the profile.</summary>
    private static void CopyAutomation(DedicatedServerProfile from, DedicatedServerProfile to)
    {
        to.AutoRestartOnCrash = from.AutoRestartOnCrash;
        to.StartWithApp = from.StartWithApp;
        to.StartMinimized = from.StartMinimized;
        to.DailyRestartEnabled = from.DailyRestartEnabled;
        to.DailyRestartTime = from.DailyRestartTime;
        to.UpdateOnRestart = from.UpdateOnRestart;
        to.AutoUpdate = from.AutoUpdate;
    }

    // ---- Overview tiles (selected server) ----

    [ObservableProperty] private string _playersTile = "0";
    [ObservableProperty] private string _playersHint = "";
    [ObservableProperty] private string _worldTile = "";
    [ObservableProperty] private string _worldHint = "";
    [ObservableProperty] private string _uptimeTile = "";
    [ObservableProperty] private string _uptimeHint = "";
    [ObservableProperty] private string _memoryTile = "";
    [ObservableProperty] private string _memoryHint = "";
    [ObservableProperty] private string _buildTile = "";
    [ObservableProperty] private string _buildHint = "";
    [ObservableProperty] private string _backupTile = "";
    [ObservableProperty] private string _backupHint = "";

    public ObservableCollection<string> NextUp { get; } = [];

    private void RefreshOverview()
    {
        if (SelectedProfile is not { } p)
        {
            NextUp.Clear();
            return;
        }

        var maxPlayers = int.TryParse(MaxPlayersText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp) ? mp : DedicatedServerDefaults.MaxPlayers;
        PlayersTile = Running is null ? "-" : $"{OnlinePlayers.Count} / {maxPlayers}";
        PlayersHint = Running is null ? "server stopped" : OnlinePlayers.Count == 0 ? "nobody online" : string.Join(", ", OnlinePlayers.Select(o => o.Name));

        WorldTile = Running is null ? "-" : CurrentProspect ?? "Lobby";
        WorldHint = CurrentProspect is null ? $"last: {LastProspectName ?? "none yet"}" : "loaded prospect";

        if (Running is { } r)
        {
            UptimeTile = FormatUptime(DateTime.Now - r.StartTime);
            UptimeHint = $"started {r.StartTime:HH:mm}";
            var bytes = _processes.GetMemoryBytes(r.ProcessId);
            MemoryTile = bytes is { } b ? $"{b / (1024.0 * 1024 * 1024):0.0} GB" : "-";
            MemoryHint = $"PID {r.ProcessId}";
        }
        else
        {
            UptimeTile = "-";
            UptimeHint = "not running";
            MemoryTile = "-";
            MemoryHint = "";
        }

        BuildTile = InstalledBuildId is null ? "Unknown"
            : UpdateAvailable ? "Update available"
            : LatestBuildId is not null ? "Up to date"
            : InstalledBuildId.Value.ToString(CultureInfo.InvariantCulture);
        BuildHint = InstalledBuildId is null ? "no Steam manifest" : $"build {InstalledBuildId} · {InstalledBranch ?? "public"}";

        var newest = Backups.OrderByDescending(b => b.CreatedLocal).FirstOrDefault();
        BackupTile = newest is null ? "None" : newest.CreatedLocal.Date == DateTime.Today ? newest.CreatedLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : newest.CreatedLocal.ToString("d MMM", CultureInfo.CurrentCulture);
        BackupHint = $"{Backups.Count} kept";

        var watch = _watches.GetValueOrDefault(p.Id);
        var players = watch?.Events.Online.Count ?? OnlinePlayers.Count;
        var lines = (watch?.Automation ?? new ServerAutomation()).DescribeUpcoming(p, Clock(), players).ToList();
        if (watch?.LastAutomationEvent is { } last)
        {
            lines.Add($"Last: {last}");
        }

        if (!NextUp.SequenceEqual(lines))
        {
            NextUp.Clear();
            foreach (var line in lines)
            {
                NextUp.Add(line);
            }
        }
    }

    // ---- The automation beat ----

    /// <summary>Called by Tick every second: minimizes consoles still waiting for it, and runs the automation beat every few seconds.</summary>
    internal void AutomationTick()
    {
        foreach (var watch in _watches.Values.Where(w => w.MinimizeUntil is not null).ToList())
        {
            TryMinimize(watch);
        }

        if (++_tickCount % AutomationEveryTicks == 0)
        {
            _ = RunAutomationBeatAsync();
        }
    }

    private void TryMinimize(ServerWatch watch)
    {
        if (watch.MinimizeUntil is not { } until || Clock() > until)
        {
            watch.MinimizeUntil = null;
            return;
        }

        try
        {
            if (_processes.FindRunning(watch.Profile) is { } running && _processes.TryMinimizeConsole(running.ProcessId))
            {
                watch.MinimizeUntil = null;
            }
        }
        catch (Exception)
        {
            // Not there yet; try again next second until the deadline.
        }
    }

    private ServerWatch GetWatch(DedicatedServerProfile profile)
    {
        if (!_watches.TryGetValue(profile.Id, out var watch))
        {
            watch = new ServerWatch(profile);
            _watches[profile.Id] = watch;
        }

        watch.Profile = profile;
        var path = DedicatedServerLayout.LogPath(profile.InstallFolder, profile.SavedDirSuffix, profile.LogFileName);
        if (!string.Equals(watch.TailerPath, path, StringComparison.OrdinalIgnoreCase))
        {
            watch.TailerPath = path;
            watch.Tailer = new LogTailer(path);
            watch.Events.Reset();
            FeedWatch(watch, watch.Tailer.Poll(WatchFirstReadBytes));
        }

        return watch;
    }

    private static void FeedWatch(ServerWatch watch, IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (ServerLogFilter.IsCleanExit(line))
            {
                watch.CleanExitSeen = true;
            }

            watch.Events.Feed(line);
        }
    }

    /// <summary>One look at every server: what changed, and what (if anything) automation should do about it. Never two at once.</summary>
    internal async Task RunAutomationBeatAsync()
    {
        if (_automationRunning)
        {
            return;
        }

        _automationRunning = true;
        try
        {
            foreach (var profile in Profiles.ToList())
            {
                // The owner is doing something to this server (or SteamCMD is busy): look again next beat.
                if (IsSteamCmdBusy || (IsBusy && profile.Id == SelectedProfile?.Id))
                {
                    continue;
                }

                var watch = GetWatch(profile);
                FeedWatch(watch, watch.Tailer!.Poll());
                watch.Events.ExpireStale(DateTime.UtcNow);

                RunningServer? found;
                try
                {
                    found = _processes.FindRunning(profile);
                }
                catch (Exception)
                {
                    continue;
                }

                if (found is null && watch.Running is not null && ++watch.MissingBeats < 2)
                {
                    // Gone for one beat only could be the launcher handing over to the real server: confirm next beat.
                    continue;
                }

                watch.MissingBeats = 0;
                if (found is not null && found.ProcessId != watch.Running?.ProcessId)
                {
                    watch.CleanExitSeen = false; // a new run; the last run's exit lines no longer count
                }

                if (found is null && watch.Running is not null)
                {
                    watch.Events.Reset();
                }

                watch.Running = found;
                var action = watch.Automation.Evaluate(profile, new ServerObservation(Clock(), found is not null, watch.Events.Online.Count, watch.CleanExitSeen));
                if (action.Kind != AutomationActionKind.None)
                {
                    await ExecuteAutomationAsync(watch, action);
                }
            }
        }
        catch (Exception ex)
        {
            _activityLog.Log($"Server automation hit a problem: {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            _automationRunning = false;
            RefreshOverview();
        }
    }

    private async Task ExecuteAutomationAsync(ServerWatch watch, AutomationAction action)
    {
        var p = watch.Profile;
        switch (action.Kind)
        {
            case AutomationActionKind.RestartAfterCrash:
                ReportAutomation(watch, $"'{p.Name}': {action.Reason}. Starting it again.", ActivityEntryKind.Warning);
                await LaunchProfileAsync(watch);
                break;

            case AutomationActionKind.GaveUpAfterCrashes:
                ReportAutomation(watch, $"'{p.Name}': {action.Reason}.", ActivityEntryKind.Warning);
                break;

            case AutomationActionKind.ScheduledRestart:
                ReportAutomation(watch, $"'{p.Name}': {action.Reason}. Restarting.", ActivityEntryKind.Info);
                await AutomatedRestartAsync(watch, checkForUpdate: p.UpdateOnRestart);
                break;

            case AutomationActionKind.CheckForUpdate:
                if (await CheckProfileForUpdateAsync(watch) == true)
                {
                    ReportAutomation(watch, $"'{p.Name}': a newer server build is out. It installs as soon as nobody is online.", ActivityEntryKind.Info);
                }

                break;

            case AutomationActionKind.UpdateNow:
                ReportAutomation(watch, $"'{p.Name}': {action.Reason}. Updating.", ActivityEntryKind.Info);
                await AutomatedRestartAsync(watch, checkForUpdate: true);
                break;
        }
    }

    private void ReportAutomation(ServerWatch watch, string text, ActivityEntryKind kind)
    {
        watch.LastAutomationEvent = $"{Clock():HH:mm}  {text}";
        _activityLog.Log(text, kind);
        if (SelectedProfile?.Id == watch.Profile.Id)
        {
            StatusMessage = text;
            Events.Insert(0, $"{Clock():HH:mm}  {text}");
        }
    }

    /// <summary>Stop (if running), update when asked and Steam has something newer, start.</summary>
    private async Task AutomatedRestartAsync(ServerWatch watch, bool checkForUpdate)
    {
        if (watch.Running is { } running)
        {
            watch.Automation.NoteIntentionalStop(Clock());
            await _processes.StopAsync(running, TimeSpan.FromSeconds(60));
            watch.Running = null;
            watch.Events.Reset();
            if (SelectedProfile?.Id == watch.Profile.Id)
            {
                Tick();
            }
        }

        if (checkForUpdate)
        {
            await UpdateProfileIfNewerAsync(watch);
        }

        await LaunchProfileAsync(watch);
    }

    /// <summary>Asks Steam for the newest build of the profile's branch. True when it differs from what is installed (or the branch changed); null when it couldn't ask.</summary>
    private async Task<bool?> CheckProfileForUpdateAsync(ServerWatch watch)
    {
        var p = watch.Profile;
        if (_steamCmd?.CurrentExePath is null)
        {
            watch.Automation.NoteUpdateCheck(Clock(), updateAvailable: false);
            return null;
        }

        var selected = SelectedProfile?.Id == p.Id;
        IsSteamCmdBusy = true;
        try
        {
            var latest = await _steamCmd.GetLatestBuildIdAsync(SteamCmd.NormalizeBranch(p.Branch));
            var (installedBuild, installedBranch) = ReadManifest(p);
            var available = latest is not null
                && (installedBuild != latest
                    || (installedBranch is not null && !string.Equals(installedBranch, SteamCmd.NormalizeBranch(p.Branch), StringComparison.OrdinalIgnoreCase)));
            watch.Automation.NoteUpdateCheck(Clock(), available);
            if (selected)
            {
                LatestBuildId = latest;
            }

            return latest is null ? null : available;
        }
        catch (Exception ex)
        {
            watch.Automation.NoteUpdateCheck(Clock(), updateAvailable: false);
            _activityLog.Log($"Couldn't check '{p.Name}' for an update: {ex.Message}", ActivityEntryKind.Warning);
            return null;
        }
        finally
        {
            IsSteamCmdBusy = false;
        }
    }

    private async Task UpdateProfileIfNewerAsync(ServerWatch watch)
    {
        var p = watch.Profile;
        if (_steamCmd?.CurrentExePath is null)
        {
            ReportAutomation(watch, $"'{p.Name}': skipped the update check because SteamCMD isn't set up.", ActivityEntryKind.Warning);
            return;
        }

        if (!watch.Automation.UpdatePending && await CheckProfileForUpdateAsync(watch) != true)
        {
            return;
        }

        var backup = TryBackup(p, "before-update");
        var result = await RunSteamCmdAsync(() => _steamCmd.InstallOrUpdateAsync(p.InstallFolder, SteamCmd.NormalizeBranch(p.Branch), line => OnSteamCmdLineFor(p.Id, line)));
        watch.Automation.NoteUpdateFinished();
        ReportAutomation(watch,
            result.Success
                ? $"'{p.Name}' updated{(backup is null ? "" : "; worlds and settings were backed up first")}."
                : $"'{p.Name}': the update failed ({result.Message}). Starting the build it already had.",
            result.Success ? ActivityEntryKind.Success : ActivityEntryKind.Warning);

        if (SelectedProfile?.Id == p.Id)
        {
            ReadInstalledBuild();
            if (result.Success)
            {
                LatestBuildId = InstalledBuildId;
            }

            RefreshBackups();
        }
    }

    /// <summary>Starts a server the way Start does, minus the form save (automation never writes half-typed settings).</summary>
    private async Task<bool> LaunchProfileAsync(ServerWatch watch)
    {
        var p = watch.Profile;
        var selected = SelectedProfile?.Id == p.Id;
        if (selected)
        {
            _tailer?.SeekToEnd();
            _allLogLines.Clear();
            LogLines.Clear();
        }

        try
        {
            _processes.Start(p);
        }
        catch (Exception ex)
        {
            ReportAutomation(watch, $"Couldn't start '{p.Name}': {ex.Message}", ActivityEntryKind.Warning);
            return false;
        }

        watch.CleanExitSeen = false;
        if (p.StartMinimized)
        {
            watch.MinimizeUntil = Clock() + TimeSpan.FromMinutes(2);
        }

        for (var i = 0; i < 20; i++)
        {
            if (_processes.FindRunning(p) is not null)
            {
                break;
            }

            await Task.Delay(StartPollDelay);
        }

        if (selected)
        {
            Tick();
        }

        return true;
    }

    /// <summary>Servers marked "Start with Starlink" that aren't already running. Runs once, when the app opens.</summary>
    internal async Task StartServersMarkedForStartupAsync()
    {
        while (_automationRunning)
        {
            await Task.Delay(StartPollDelay);
        }

        _automationRunning = true;
        try
        {
            foreach (var profile in Profiles.Where(p => p.StartWithApp).ToList())
            {
                if (_processes.FindRunning(profile) is not null)
                {
                    continue;
                }

                var watch = GetWatch(profile);
                ReportAutomation(watch, $"Starting '{profile.Name}' (Start with Starlink is on).", ActivityEntryKind.Info);
                watch.Automation.NoteManualStart();
                await LaunchProfileAsync(watch);
            }
        }
        catch (Exception ex)
        {
            _activityLog.Log($"Couldn't start servers marked 'Start with Starlink': {ex.Message}", ActivityEntryKind.Warning);
        }
        finally
        {
            _automationRunning = false;
        }
    }

    private static (int? BuildId, string? Branch) ReadManifest(DedicatedServerProfile p)
    {
        var path = DedicatedServerLayout.AppManifestPath(p.InstallFolder);
        if (!File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            return SteamCmd.ReadAppManifest(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return (null, null);
        }
    }
}
