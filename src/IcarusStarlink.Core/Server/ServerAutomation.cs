using System.Globalization;

namespace IcarusStarlink.Core.Server;

public enum AutomationActionKind
{
    None,

    /// <summary>The server stopped without logging a clean exit: start it again.</summary>
    RestartAfterCrash,

    /// <summary>It crashed too often in a short time: stop restarting it and tell the owner.</summary>
    GaveUpAfterCrashes,

    /// <summary>The daily restart is due (and the server is empty, or the wait ran out).</summary>
    ScheduledRestart,

    /// <summary>Time for the hourly "is there a newer build" question to Steam.</summary>
    CheckForUpdate,

    /// <summary>A newer build is known and nobody is online: stop, update, start.</summary>
    UpdateNow,

    /// <summary>The once-a-day backup of a running server's worlds and settings is due.</summary>
    DailyBackup,
}

/// <summary>How a server's last run ended, as automation saw it.</summary>
public enum ServerStopKind
{
    /// <summary>A clean exit in its log, or a stop the app made on purpose (daily restart, update).</summary>
    Clean,

    /// <summary>The owner stopped it (Stop, or Update while it ran).</summary>
    ByOwner,

    /// <summary>The process went away without a clean exit and nobody asked it to.</summary>
    Crash,
}

public sealed record AutomationAction(AutomationActionKind Kind, string Reason)
{
    public static readonly AutomationAction None = new(AutomationActionKind.None, "");
}

/// <summary>What the app saw on one beat: the server process (and its id while running), who is online per its log, and whether its log shows a clean exit.</summary>
public readonly record struct ServerObservation(DateTime NowLocal, bool Running, int PlayersOnline, bool CleanExitLogged, int? ProcessId = null);

/// <summary>
/// The decisions behind the Overview tab's automation, for ONE server, kept free of processes,
/// timers and SteamCMD so every rule is testable: the app feeds an observation every few seconds
/// and carries out whatever action comes back, reporting results through the Note methods.
/// Icarus has no RCON, so nothing here can warn players: restarts wait for an empty server instead.
/// </summary>
public sealed class ServerAutomation
{
    public const int MaxCrashRestarts = 3;
    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RestartWaitForEmpty = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan DailyBackupInterval = TimeSpan.FromHours(24);

    /// <summary>A daily slot the app wasn't open for (or that is long past) is skipped, never run hours late.</summary>
    private static readonly TimeSpan MissedSlotGrace = TimeSpan.FromMinutes(5);

    private readonly List<DateTime> _crashRestarts = [];
    private bool? _wasRunning;
    private int? _lastProcessId;

    /// <summary>
    /// The processes the app (or the owner through the app) stopped on purpose. Kept by process id
    /// rather than for a time window: the beat that notices the exit can come much later (beats
    /// wait while SteamCMD runs an update), and a window that ran out turned an owner's Stop into
    /// a "crash" that was then restarted.
    /// </summary>
    private readonly HashSet<int> _intendedStops = [];
    private bool _intendedStopOfUnknownProcess;
    private DateTime? _lastDailySlotHandled;

    /// <summary>Crashed MaxCrashRestarts times within CrashWindow; no more restarts until someone starts it by hand.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>The last hourly check found a newer build that hasn't been installed yet.</summary>
    public bool UpdatePending { get; private set; }

    public DateTime? LastUpdateCheck { get; private set; }

    /// <summary>When the last daily backup was made (the app seeds it from the newest one on disk).</summary>
    public DateTime? LastDailyBackup { get; private set; }

    public void NoteDailyBackup(DateTime? madeLocal) => LastDailyBackup = madeLocal;

    /// <summary>
    /// The app (or the owner through the app) is stopping this process: its exit is not a crash,
    /// however long it takes the next beat to notice. Null when the process id isn't known; that
    /// excuses the next exit only.
    /// </summary>
    public void NoteIntentionalStop(int? processId)
    {
        if (processId is { } id)
        {
            _intendedStops.Add(id);
        }
        else
        {
            _intendedStopOfUnknownProcess = true;
        }
    }

    /// <summary>The owner stopped it (Stop, or Update while it ran): nothing automatic starts it again until it runs again.</summary>
    public bool StoppedByOwner { get; private set; }

    public void NoteOwnerStop(int? processId)
    {
        NoteIntentionalStop(processId);
        StoppedByOwner = true;
    }

    /// <summary>Someone pressed Start: re-arm crash restarts.</summary>
    public void NoteManualStart()
    {
        GaveUp = false;
        StoppedByOwner = false;
        _crashRestarts.Clear();
    }

    public void NoteUpdateCheck(DateTime nowLocal, bool updateAvailable)
    {
        LastUpdateCheck = nowLocal;
        UpdatePending = updateAvailable;
    }

    /// <summary>The update ran (or failed): either way, wait for the next hourly check rather than retrying every beat.</summary>
    public void NoteUpdateFinished() => UpdatePending = false;

    public AutomationAction Evaluate(DedicatedServerProfile profile, ServerObservation now)
    {
        var wasRunning = _wasRunning;
        _wasRunning = now.Running;

        if (!now.Running)
        {
            return wasRunning == true ? OnStopped(profile, now) : AutomationAction.None;
        }

        if (now.ProcessId is { } pid && pid != _lastProcessId)
        {
            // A new run (started here or outside Starlink): the owner's earlier Stop is history.
            if (_lastProcessId is not null)
            {
                StoppedByOwner = false;
            }

            _intendedStops.RemoveWhere(id => id != pid);
            _lastProcessId = pid;
        }

        if (DailyRestartDue(profile, now) is { } restart)
        {
            return restart;
        }

        if (profile.AutoUpdate)
        {
            if (UpdatePending && now.PlayersOnline == 0)
            {
                return new AutomationAction(AutomationActionKind.UpdateNow, "a newer server build is out and nobody is online");
            }

            if (!UpdatePending && (LastUpdateCheck is null || now.NowLocal - LastUpdateCheck >= UpdateCheckInterval))
            {
                // Stamped now, not when the answer comes back: a failed check waits for the next hour too.
                LastUpdateCheck = now.NowLocal;
                return new AutomationAction(AutomationActionKind.CheckForUpdate, "hourly update check");
            }
        }

        if (profile.DailyBackup && (LastDailyBackup is null || now.NowLocal - LastDailyBackup >= DailyBackupInterval))
        {
            // Stamped now: a backup that fails waits a day rather than retrying every beat.
            LastDailyBackup = now.NowLocal;
            return new AutomationAction(AutomationActionKind.DailyBackup, "daily backup");
        }

        return AutomationAction.None;
    }

    /// <summary>When the last run this saw ended (the beat that noticed it), and how; null until it has seen one end.</summary>
    public DateTime? LastStoppedAt { get; private set; }

    public ServerStopKind LastStopKind { get; private set; }

    /// <summary>Crash restarts made in the CrashWindow before nowLocal (MaxCrashRestarts are allowed).</summary>
    public int CrashRestartsInWindow(DateTime nowLocal) => _crashRestarts.Count(t => nowLocal - t <= CrashWindow);

    /// <summary>The next daily restart slot still to come (today's if it is ahead and not handled yet, else tomorrow's); null when the daily restart is off or its time isn't one.</summary>
    public DateTime? NextDailyRestart(DedicatedServerProfile profile, DateTime nowLocal)
    {
        if (!profile.DailyRestartEnabled || !TryParseDailyTime(profile.DailyRestartTime, out var time))
        {
            return null;
        }

        var today = DateOnly.FromDateTime(nowLocal).ToDateTime(time);
        return nowLocal < today && !(_lastDailySlotHandled >= today) ? today : today.AddDays(1);
    }

    /// <summary>When the hourly update check is next due (it only runs while the server runs); null before the first one, which comes on the next beat that sees it running.</summary>
    public DateTime? NextUpdateCheck => LastUpdateCheck + UpdateCheckInterval;

    /// <summary>When the next daily backup is due (it only runs while the server runs); null when none was made yet, so it is due on the next beat that sees it running.</summary>
    public DateTime? NextDailyBackup => LastDailyBackup + DailyBackupInterval;

    private AutomationAction OnStopped(DedicatedServerProfile profile, ServerObservation now)
    {
        var intended = now.CleanExitLogged
            || _intendedStopOfUnknownProcess
            || (_lastProcessId is { } pid && _intendedStops.Remove(pid));
        _intendedStopOfUnknownProcess = false;
        LastStoppedAt = now.NowLocal;
        LastStopKind = StoppedByOwner ? ServerStopKind.ByOwner : intended ? ServerStopKind.Clean : ServerStopKind.Crash;
        if (intended || StoppedByOwner || !profile.AutoRestartOnCrash || GaveUp)
        {
            return AutomationAction.None;
        }

        _crashRestarts.RemoveAll(t => now.NowLocal - t > CrashWindow);
        if (_crashRestarts.Count >= MaxCrashRestarts)
        {
            GaveUp = true;
            return new AutomationAction(AutomationActionKind.GaveUpAfterCrashes,
                $"it crashed {MaxCrashRestarts + 1} times in {CrashWindow.TotalMinutes:0} minutes, so it was left stopped. Check its log, then Start it to re-arm crash restarts");
        }

        _crashRestarts.Add(now.NowLocal);
        return new AutomationAction(AutomationActionKind.RestartAfterCrash,
            $"it stopped without a clean exit (restart {_crashRestarts.Count} of {MaxCrashRestarts} allowed in {CrashWindow.TotalMinutes:0} minutes)");
    }

    private AutomationAction? DailyRestartDue(DedicatedServerProfile profile, ServerObservation now)
    {
        if (!profile.DailyRestartEnabled || !TryParseDailyTime(profile.DailyRestartTime, out var time))
        {
            return null;
        }

        // Yesterday's slot too: a 23:30 restart that waits for players is still due at 00:10.
        var today = DateOnly.FromDateTime(now.NowLocal);
        foreach (var day in new[] { today.AddDays(-1), today })
        {
            var slot = day.ToDateTime(time);
            if (now.NowLocal < slot || _lastDailySlotHandled >= slot)
            {
                continue;
            }

            var forceAt = slot + RestartWaitForEmpty;
            if (now.NowLocal > forceAt + MissedSlotGrace)
            {
                _lastDailySlotHandled = slot;
                continue;
            }

            if (now.PlayersOnline == 0)
            {
                _lastDailySlotHandled = slot;
                return new AutomationAction(AutomationActionKind.ScheduledRestart, $"daily restart ({time:HH\\:mm})");
            }

            if (now.NowLocal >= forceAt)
            {
                _lastDailySlotHandled = slot;
                return new AutomationAction(AutomationActionKind.ScheduledRestart,
                    $"daily restart ({time:HH\\:mm}); players were still online after waiting {RestartWaitForEmpty.TotalMinutes:0} minutes");
            }
        }

        return null;
    }

    /// <summary>The daily restart waiting for players to leave right now, as the time it will go ahead anyway; null when not waiting.</summary>
    public DateTime? WaitingForEmptyUntil(DedicatedServerProfile profile, DateTime nowLocal)
    {
        if (!profile.DailyRestartEnabled || !TryParseDailyTime(profile.DailyRestartTime, out var time))
        {
            return null;
        }

        var today = DateOnly.FromDateTime(nowLocal);
        foreach (var day in new[] { today.AddDays(-1), today })
        {
            var slot = day.ToDateTime(time);
            if (nowLocal >= slot && nowLocal <= slot + RestartWaitForEmpty && !(_lastDailySlotHandled >= slot))
            {
                return slot + RestartWaitForEmpty;
            }
        }

        return null;
    }

    /// <summary>"Next up" lines for the Overview tab.</summary>
    public IReadOnlyList<string> DescribeUpcoming(DedicatedServerProfile profile, DateTime nowLocal, int playersOnline)
    {
        var lines = new List<string>();
        if (profile.DailyRestartEnabled)
        {
            if (!TryParseDailyTime(profile.DailyRestartTime, out var time))
            {
                lines.Add($"Daily restart is on, but '{profile.DailyRestartTime}' isn't a time. Use 24-hour HH:mm, like 05:00.");
            }
            else if (WaitingForEmptyUntil(profile, nowLocal) is { } until && playersOnline > 0)
            {
                lines.Add($"Daily restart is waiting for {playersOnline} player(s) to leave. It goes ahead anyway at {until:HH:mm}.");
            }
            else if (WaitingForEmptyUntil(profile, nowLocal) is { } goesAt)
            {
                lines.Add($"Daily restart is due now: it goes ahead as soon as the server is empty, or at {goesAt:HH:mm} at the latest.");
            }
            else if (NextDailyRestart(profile, nowLocal) is { } next)
            {
                lines.Add($"Next daily restart {DayWord(next, nowLocal)} at {next:HH:mm}{(profile.UpdateOnRestart ? ", with an update check" : "")}.");
            }
        }

        if (profile.AutoUpdate)
        {
            lines.Add(UpdatePending
                ? "A newer build is out. It installs as soon as nobody is online."
                : NextUpdateCheck is { } check && check > nowLocal
                    ? $"Next update check at {check:HH:mm} (only while it runs)."
                    : "Checks for an update as soon as it runs.");
        }

        if (profile.DailyBackup)
        {
            lines.Add(NextDailyBackup is { } backup && backup > nowLocal
                ? $"Next daily backup {DayWord(backup, nowLocal)} around {backup:HH:mm} (only while it runs)."
                : "Backs up its worlds as soon as it runs (daily backup).");
        }

        if (profile.AutoRestartOnCrash)
        {
            var used = CrashRestartsInWindow(nowLocal);
            lines.Add(GaveUp
                ? $"Crash restarts are paused after {MaxCrashRestarts + 1} crashes in {CrashWindow.TotalMinutes:0} minutes. Start it to re-arm them."
                : used > 0
                    ? $"Restarts after a crash: {used} of {MaxCrashRestarts} restarts used in the last {CrashWindow.TotalMinutes:0} minutes."
                    : $"Restarts after a crash, up to {MaxCrashRestarts} times in {CrashWindow.TotalMinutes:0} minutes.");
        }

        if (lines.Count == 0)
        {
            lines.Add("No automation is on for this server.");
        }

        return lines;
    }

    /// <summary>"today", "tomorrow" or the day's name, for a time after nowLocal.</summary>
    private static string DayWord(DateTime when, DateTime nowLocal) =>
        when.Date == nowLocal.Date ? "today"
        : when.Date == nowLocal.Date.AddDays(1) ? "tomorrow"
        : "on " + when.ToString("dddd", CultureInfo.InvariantCulture);

    public static bool TryParseDailyTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact((text ?? "").Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
