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

    /// <summary>How long automation's update check waits for SteamCMD before giving up (it normally takes seconds). Tests shorten it.</summary>
    internal TimeSpan UpdateCheckTimeout { get; set; } = TimeSpan.FromMinutes(5);

    // ---- Automation settings (saved straight into the profile, no Save button) ----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationSummary))] private bool _autoRestartOnCrash;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationSummary))] private bool _startWithApp;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationSummary))] private bool _startMinimized;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationSummary))] private bool _dailyRestartEnabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationSummary))] private string _dailyRestartTime = "05:00";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationSummary))] private bool _updateOnRestart;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationSummary))] private bool _autoUpdate;

    /// <summary>The Automation section's one-line header: which of its options are on, or "All off".</summary>
    public string AutomationSummary
    {
        get
        {
            var on = new List<string>();
            if (AutoRestartOnCrash)
            {
                on.Add("crash restart");
            }

            if (DailyRestartEnabled)
            {
                on.Add($"daily restart {DailyRestartTime}");
            }

            if (UpdateOnRestart)
            {
                on.Add("update on restart");
            }

            if (AutoUpdate)
            {
                on.Add("auto-update");
            }

            if (DailyBackup)
            {
                on.Add("daily backup");
            }

            if (StartWithApp)
            {
                on.Add("starts with Starlink");
            }

            if (StartMinimized)
            {
                on.Add("minimized");
            }

            return on.Count == 0 ? "All off" : "On: " + string.Join(" · ", on);
        }
    }

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
            DailyBackup = p?.DailyBackup ?? false;
            BackupsToKeepText = p is null || p.BackupsToKeep <= 0 ? "0" : p.BackupsToKeep.ToString(CultureInfo.InvariantCulture);
            BackupsToKeepError = null;
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
        to.DailyBackup = from.DailyBackup;
        to.BackupsToKeep = from.BackupsToKeep;
    }

    // ---- The automation beat ----

    /// <summary>Called by Tick every second: minimizes consoles still waiting for it, and runs the automation beat every few seconds.</summary>
    internal void AutomationTick()
    {
        foreach (var watch in _watches.Values.Where(w => w.MinimizeUntil is not null && !w.Minimizing).ToList())
        {
            _ = TryMinimizeAsync(watch);
        }

        if (++_tickCount % AutomationEveryTicks == 0)
        {
            _ = RunAutomationBeatAsync();
        }
    }

    /// <summary>The process lookup runs off the UI thread; one attempt per server at a time.</summary>
    private async Task TryMinimizeAsync(ServerWatch watch)
    {
        if (watch.MinimizeUntil is not { } until || Clock() > until)
        {
            watch.MinimizeUntil = null;
            return;
        }

        watch.Minimizing = true;
        try
        {
            var profile = watch.Profile;
            if (await OffUiThread(() => _processes.FindRunning(profile) is { } running && _processes.TryMinimizeConsole(running.ProcessId)))
            {
                watch.MinimizeUntil = null;
            }
        }
        catch (Exception)
        {
            // Not there yet; try again next second until the deadline.
        }
        finally
        {
            watch.Minimizing = false;
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
                // The owner is doing something to this server (starting, stopping, updating, a backup,
                // restore or mod change), or SteamCMD is busy: look again next beat.
                if (IsSteamCmdBusy || (IsBusy && profile.Id == SelectedProfile?.Id) || (IsBackupBusy && profile.Id == _backupBusyFor))
                {
                    continue;
                }

                var watch = GetWatch(profile);
                await EnsureFirstReadAsync(watch);
                await PollWatchAsync(watch);
                ExpireStalePlayers(watch);

                RunningServer? found;
                try
                {
                    found = await OffUiThread(() => _processes.FindRunning(profile));
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
                    ResetWatchEvents(watch);
                }

                var wasRunning = watch.Running is not null;
                var wasObserved = watch.Observed;
                watch.Observed = true;
                watch.Running = found;
                var action = watch.Automation.Evaluate(profile, new ServerObservation(Clock(), found is not null, watch.Events.Online.Count, watch.CleanExitSeen, found?.ProcessId));
                if (found is not null && !wasRunning && wasObserved)
                {
                    RecordEventLine(watch, $"{Clock():HH:mm}  Server started");
                }
                else if (found is null && wasRunning)
                {
                    RecordEventLine(watch, $"{Clock():HH:mm}  " + watch.Automation.LastStopKind switch
                    {
                        ServerStopKind.ByOwner => "Server stopped by you",
                        ServerStopKind.Clean => "Server stopped cleanly",
                        _ => "Server crashed (it stopped without a clean exit in its log)",
                    });
                }

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
            // Another profile on this install may have started or stopped: its Mods buttons follow.
            RaiseServerFilesChanged();
        }
    }

    private async Task ExecuteAutomationAsync(ServerWatch watch, AutomationAction action)
    {
        var p = watch.Profile;
        switch (action.Kind)
        {
            case AutomationActionKind.RestartAfterCrash:
                ReportAutomation(watch, $"'{p.Name}': {action.Reason}. Starting it again.", ActivityEntryKind.Warning);
                await RunAsAutomationAsync(watch, "restarting it after a crash", () => LaunchIfStillWantedAsync(watch));
                break;

            case AutomationActionKind.GaveUpAfterCrashes:
                ReportAutomation(watch, $"'{p.Name}': {action.Reason}.", ActivityEntryKind.Warning);
                break;

            case AutomationActionKind.ScheduledRestart:
                ReportAutomation(watch, $"'{p.Name}': {action.Reason}. Restarting.", ActivityEntryKind.Info);
                await RunAsAutomationAsync(watch, p.UpdateOnRestart ? "doing the daily restart (with an update check)" : "doing the daily restart", () => AutomatedRestartAsync(watch, checkForUpdate: p.UpdateOnRestart));
                break;

            case AutomationActionKind.CheckForUpdate:
                if (await CheckProfileForUpdateAsync(watch) == true)
                {
                    ReportAutomation(watch, $"'{p.Name}': a newer server build is out. It installs as soon as nobody is online.", ActivityEntryKind.Info);
                }

                break;

            case AutomationActionKind.UpdateNow:
                // Checked before stopping anything: otherwise the server was stopped, the update
                // skipped and the server started again on every beat.
                if (await SkipUpdateForRunningSiblingsAsync(watch))
                {
                    break;
                }

                ReportAutomation(watch, $"'{p.Name}': {action.Reason}. Updating.", ActivityEntryKind.Info);
                await RunAsAutomationAsync(watch, "stopping it to install a newer build, then starting it again", () => AutomatedRestartAsync(watch, checkForUpdate: true));
                break;

            case AutomationActionKind.DailyBackup:
                var made = await TryBackupAsync(p, ServerBackups.DailyReason);
                if (made is null)
                {
                    ReportAutomation(watch, $"'{p.Name}': the daily backup failed (see the activity log). It tries again tomorrow.", ActivityEntryKind.Warning);
                }
                else
                {
                    RecordEventLine(watch, $"{Clock():HH:mm}  Daily backup made: {Path.GetFileName(made)}");
                    _activityLog.Log($"'{p.Name}': daily backup made ({Path.GetFileName(made)}).", ActivityEntryKind.Info);
                    if (SelectedProfile?.Id == p.Id)
                    {
                        RefreshBackups();
                    }
                }

                break;
        }
    }

    private void ReportAutomation(ServerWatch watch, string text, ActivityEntryKind kind)
    {
        _activityLog.Log(text, kind);
        RecordEventLine(watch, $"{Clock():HH:mm}  {text}");
        if (SelectedProfile?.Id == watch.Profile.Id)
        {
            StatusMessage = text;
        }
    }

    /// <summary>An automated stop/update/start is running on this server: the Start/Stop/Update buttons wait for it, and so does an app update.</summary>
    public bool IsAutomationBusy => SelectedProfile is { } p && _watches.TryGetValue(p.Id, out var w) && w.AutomationBusy;

    /// <summary>Any server is being restarted or updated by automation right now.</summary>
    internal bool AnyAutomationBusy => _watches.Values.Any(w => w.AutomationBusy);

    private async Task RunAsAutomationAsync(ServerWatch watch, string doing, Func<Task> work)
    {
        watch.AutomationDoing = doing;
        watch.AutomationBusy = true;
        RaiseAutomationBusyChanged();
        try
        {
            await work();
        }
        finally
        {
            watch.AutomationBusy = false;
            watch.AutomationDoing = null;
            RaiseAutomationBusyChanged();
        }
    }

    private void RaiseAutomationBusyChanged()
    {
        OnPropertyChanged(nameof(IsAutomationBusy));
        OnPropertyChanged(nameof(RunStateText));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        UpdateServerCommand.NotifyCanExecuteChanged();
        RaiseServerFilesChanged();
    }

    /// <summary>
    /// Right before automation starts a server: the owner may have stopped it while automation was
    /// working (an update can take minutes), or started it themselves. Either way, don't launch.
    /// </summary>
    private async Task<bool> LaunchIfStillWantedAsync(ServerWatch watch)
    {
        var p = watch.Profile;
        if (watch.Automation.StoppedByOwner)
        {
            ReportAutomation(watch, $"'{p.Name}' was left stopped: you stopped it, so automation doesn't start it again.", ActivityEntryKind.Info);
            return false;
        }

        RunningServer? already;
        try
        {
            already = await OffUiThread(() => _processes.FindRunning(p));
        }
        catch (Exception)
        {
            already = null;
        }

        if (already is not null)
        {
            watch.Running = already;
            return false; // started meanwhile (by hand, or from outside Starlink)
        }

        return await LaunchProfileAsync(watch);
    }

    /// <summary>Stop (if running), update when asked and Steam has something newer, start.</summary>
    private async Task AutomatedRestartAsync(ServerWatch watch, bool checkForUpdate)
    {
        // Looked up again: the watch's Running is from the last beat, up to several seconds old.
        RunningServer? running;
        try
        {
            running = await OffUiThread(() => _processes.FindRunning(watch.Profile));
        }
        catch (Exception)
        {
            running = watch.Running;
        }

        if (running is not null)
        {
            watch.Automation.NoteIntentionalStop(running.ProcessId);
            await _processes.StopAsync(running, TimeSpan.FromSeconds(60));
            watch.Running = null;
            ResetWatchEvents(watch);
            if (SelectedProfile?.Id == watch.Profile.Id)
            {
                await TickAsync();
            }
        }

        if (checkForUpdate)
        {
            await UpdateProfileIfNewerAsync(watch);
        }

        await LaunchIfStillWantedAsync(watch);
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

        await WaitForSteamCmdAsync();
        var box = KeepSteamCmdBoxTitle();
        var token = BeginSteamCmdRun($"Checking '{p.Name}' for an update", p.Id);
        SteamCmdStatus = "asking Steam for the latest build…";
        IsSteamCmdBusy = true;
        // A check takes seconds. One that hangs would keep SteamCMD busy for good, and every beat
        // skips while it is, so crash and daily restarts of every server would stop. It gives up
        // after UpdateCheckTimeout instead and says so in Recent events.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(UpdateCheckTimeout);
        try
        {
            var latest = await _steamCmd.GetLatestBuildIdAsync(SteamCmd.NormalizeBranch(p.Branch), timeout.Token);
            var (installedBuild, installedBranch) = ReadManifest(p);
            var available = latest is not null
                && (installedBuild != latest
                    || (installedBranch is not null && !string.Equals(installedBranch, SteamCmd.NormalizeBranch(p.Branch), StringComparison.OrdinalIgnoreCase)));
            watch.Automation.NoteUpdateCheck(Clock(), available);

            // Looked at after the answer: the owner may have picked another server or branch meanwhile.
            if (SelectedProfile?.Id == p.Id && string.Equals(SteamCmd.NormalizeBranch(Branch), SteamCmd.NormalizeBranch(p.Branch), StringComparison.Ordinal))
            {
                LatestBuildId = latest;
            }

            return latest is null ? null : available;
        }
        catch (Exception ex)
        {
            watch.Automation.NoteUpdateCheck(Clock(), updateAvailable: false);
            var why = ex is OperationCanceledException && !token.IsCancellationRequested
                ? $"SteamCMD didn't answer within {UpdateCheckTimeout.TotalMinutes:0} minutes"
                : ex is OperationCanceledException ? "cancelled" : ex.Message;
            _activityLog.Log($"Couldn't check '{p.Name}' for an update: {why}", ActivityEntryKind.Warning);
            RecordEventLine(watch, $"{Clock():HH:mm}  Couldn't check for an update ({why}). It tries again in an hour.");
            return null;
        }
        finally
        {
            IsSteamCmdBusy = false;
            SteamCmdStatus = null;
            box();
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

        if (await SkipUpdateForRunningSiblingsAsync(watch))
        {
            return;
        }

        var backup = await TryBackupAsync(p, "before-update");
        var result = await RunSteamCmdAsync($"Updating '{p.Name}'", p.Id, token => _steamCmd.InstallOrUpdateAsync(p.InstallFolder, SteamCmd.NormalizeBranch(p.Branch), line => OnSteamCmdLineFor(p.Id, line), token));
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

    /// <summary>
    /// Another profile on the same install is running: the server build is shared, so its files
    /// can't be replaced under it. Reported, and the next hourly check finds the update again.
    /// </summary>
    private async Task<bool> SkipUpdateForRunningSiblingsAsync(ServerWatch watch)
    {
        var p = watch.Profile;
        if (DescribeRunningSiblings(p, await OffUiThread(() => RunningSiblings(p)), "update it") is not { } refusal)
        {
            return false;
        }

        watch.Automation.NoteUpdateCheck(Clock(), updateAvailable: false);
        ReportAutomation(watch, $"'{p.Name}': skipped the update. {refusal}", ActivityEntryKind.Warning);
        return true;
    }

    /// <summary>Starts a server the way Start does, minus the form save (automation never writes half-typed settings).</summary>
    private async Task<bool> LaunchProfileAsync(ServerWatch watch)
    {
        var p = watch.Profile;
        var selected = SelectedProfile?.Id == p.Id;
        await StartLogAfreshAsync(watch);

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
            if (await OffUiThread(() => _processes.FindRunning(p)) is not null)
            {
                break;
            }

            await Task.Delay(StartPollDelay);
        }

        if (selected)
        {
            await TickAsync();
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
                if (await OffUiThread(() => _processes.FindRunning(profile)) is not null)
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
