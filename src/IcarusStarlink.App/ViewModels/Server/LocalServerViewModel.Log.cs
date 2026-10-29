using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Reading each server's log, once. A server's watch owns its one LogTailer: automation reads it
/// for every server (who is online, did the last run exit cleanly), and the Log, Players and
/// Events panels show the selected server's watch instead of reading the same file a second
/// time. The first read of a long-running server's log (the developer's own is 8.9 MB) and every
/// later poll happen off the UI thread; only the small step of applying new lines runs on it.
/// </summary>
public sealed partial class LocalServerViewModel
{
    private sealed class ServerWatch(DedicatedServerProfile profile)
    {
        public DedicatedServerProfile Profile { get; set; } = profile;
        public LogTailer? Tailer { get; set; }
        public string? TailerPath { get; set; }

        /// <summary>Who is online and the current prospect, from this run's log.</summary>
        public ServerLogEvents Events { get; set; } = new();

        /// <summary>Newest first, the same text the Events panel shows.</summary>
        public List<string> EventLines { get; set; } = [];

        /// <summary>The newest log lines (password masked), what the Log panel shows when this server is selected.</summary>
        public List<string> RecentLines { get; set; } = [];

        /// <summary>The whole-log read made once per log file; null until it has started.</summary>
        public Task? FirstRead { get; set; }

        /// <summary>One read (and its apply) at a time, so lines are never applied out of order.</summary>
        public SemaphoreSlim PollGate { get; } = new(1, 1);

        public ServerAutomation Automation { get; } = new();
        public bool CleanExitSeen { get; set; }
        public RunningServer? Running { get; set; }
        public int MissingBeats { get; set; }
        public DateTime? MinimizeUntil { get; set; }
        public bool Minimizing { get; set; }
        public string? LastAutomationEvent { get; set; }

        /// <summary>Automation is stopping, updating or starting this server right now.</summary>
        public bool AutomationBusy { get; set; }
    }

    /// <summary>The watch whose log, players and events the panels show (the selected server's, once its first read is in).</summary>
    private ServerWatch? _shownWatch;
    private int _attachVersion;
    private long? _memoryBytes;
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    /// <summary>The selected server's log being read in. Tests await it instead of racing it.</summary>
    internal Task LogLoadTask { get; private set; } = Task.CompletedTask;

    private ServerWatch GetWatch(DedicatedServerProfile profile)
    {
        if (!_watches.TryGetValue(profile.Id, out var watch))
        {
            watch = new ServerWatch(profile);
            _watches[profile.Id] = watch;
            try
            {
                watch.Automation.NoteDailyBackup(ServerBackups.LastDailyBackup(ServerBackupsFolder(profile)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No backups folder to look in: the first daily backup happens soon.
            }
        }

        watch.Profile = profile;
        var path = DedicatedServerLayout.LogPath(profile.InstallFolder, profile.SavedDirSuffix, profile.LogFileName);
        if (!string.Equals(watch.TailerPath, path, StringComparison.OrdinalIgnoreCase))
        {
            // A different log file (new profile, or its log name / Saved suffix changed): start over.
            watch.TailerPath = path;
            watch.Tailer = new LogTailer(path);
            watch.Events = new ServerLogEvents();
            watch.EventLines = [];
            watch.RecentLines = [];
            watch.FirstRead = null;
        }

        return watch;
    }

    /// <summary>Reads the whole current log once, so players who joined hours ago are known before anything restarts the server.</summary>
    private Task EnsureFirstReadAsync(ServerWatch watch) => watch.FirstRead ??= ReadWholeLogAsync(watch);

    private sealed record LogDigest(ServerLogEvents Events, List<string> EventLines, bool CleanExit, List<string> RecentLines);

    private async Task ReadWholeLogAsync(ServerWatch watch)
    {
        await watch.PollGate.WaitAsync();
        try
        {
            var tailer = watch.Tailer!;
            var digest = await OffUiThread(() => Digest(tailer.Poll(WatchFirstReadBytes)));
            if (!ReferenceEquals(watch.Tailer, tailer))
            {
                return; // the profile's log file changed while this was reading
            }

            watch.Events = digest.Events;
            watch.EventLines = digest.EventLines;
            watch.CleanExitSeen |= digest.CleanExit;
            watch.RecentLines = digest.RecentLines;
        }
        finally
        {
            watch.PollGate.Release();
        }
    }

    /// <summary>The whole-log read's work, off the UI thread: mask passwords, feed every line to a fresh ServerLogEvents, keep the newest lines.</summary>
    private static LogDigest Digest(IReadOnlyList<string> raw)
    {
        var events = new ServerLogEvents();
        var eventLines = new List<string>();
        var clean = false;
        var lines = new List<string>(raw.Count);
        foreach (var rawLine in raw)
        {
            var line = ServerLogFilter.Redact(rawLine);
            lines.Add(line);
            clean |= ServerLogFilter.IsCleanExit(line);
            foreach (var ev in events.Feed(line).Where(e => e.Kind != ServerLogEventKind.Error))
            {
                eventLines.Add($"{ev.Time?.ToLocalTime().ToString("HH:mm") ?? "--:--"}  {ev.Text}");
            }
        }

        eventLines.Reverse();
        if (eventLines.Count > MaxEvents)
        {
            eventLines.RemoveRange(MaxEvents, eventLines.Count - MaxEvents);
        }

        var recent = lines.Count > MaxLogLines ? lines.GetRange(lines.Count - MaxLogLines, MaxLogLines) : lines;
        return new LogDigest(events, eventLines, clean, recent);
    }

    /// <summary>New lines since the last read: read off the UI thread, applied on it.</summary>
    private async Task PollWatchAsync(ServerWatch watch)
    {
        await watch.PollGate.WaitAsync();
        try
        {
            var tailer = watch.Tailer!;
            var lines = await OffUiThread(() => (IReadOnlyList<string>)[.. tailer.Poll().Select(ServerLogFilter.Redact)]);
            if (ReferenceEquals(watch.Tailer, tailer))
            {
                ApplyLines(watch, lines);
            }
        }
        finally
        {
            watch.PollGate.Release();
        }
    }

    private void ApplyLines(ServerWatch watch, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var line in lines)
        {
            if (ServerLogFilter.IsCleanExit(line))
            {
                watch.CleanExitSeen = true;
            }

            changed |= RecordEvents(watch, watch.Events.Feed(line));
        }

        watch.RecentLines.AddRange(lines);
        if (watch.RecentLines.Count > MaxLogLines)
        {
            watch.RecentLines.RemoveRange(0, watch.RecentLines.Count - MaxLogLines);
        }

        if (ReferenceEquals(_shownWatch, watch))
        {
            if (changed)
            {
                RefreshOnlinePlayers(watch);
            }

            AppendLog(lines);
        }
    }

    /// <summary>A start: what's in the log now belongs to the last run, so skip it — the new run's log is read from its first line.</summary>
    private async Task StartLogAfreshAsync(ServerWatch watch)
    {
        await watch.PollGate.WaitAsync();
        try
        {
            watch.Tailer?.SeekToEnd();
            watch.RecentLines.Clear();
            watch.FirstRead ??= Task.CompletedTask;
            watch.Events.Reset();
        }
        finally
        {
            watch.PollGate.Release();
        }

        if (ReferenceEquals(_shownWatch, watch))
        {
            _allLogLines.Clear();
            LogLines.Clear();
            RefreshOnlinePlayers(watch);
        }
    }

    /// <summary>Shows the selected server's log, players and events (reading its log first if no one has yet).</summary>
    private void AttachLog() => LogLoadTask = AttachLogAsync();

    private async Task AttachLogAsync()
    {
        var version = ++_attachVersion;
        _shownWatch = null;
        _allLogLines.Clear();
        LogLines.Clear();
        ShowPlayersAndEventsOf(null);
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        var watch = GetWatch(profile);
        try
        {
            await EnsureFirstReadAsync(watch);
        }
        catch (Exception)
        {
            // An unreadable log shows as empty; the next poll tries again.
        }

        if (version != _attachVersion || SelectedProfile?.Id != profile.Id || !ReferenceEquals(_watches.GetValueOrDefault(profile.Id), watch))
        {
            return; // another server was selected (or this one re-attached) meanwhile
        }

        _shownWatch = watch;
        ShowPlayersAndEventsOf(watch);
        _allLogLines.AddRange(watch.RecentLines);
        RebuildVisibleLog();

        RefreshOverview();
    }

    /// <summary>One heartbeat for the selected server: is it running (looked up off the UI thread), and what did it log since last time.</summary>
    public void Tick() => _ = TickAsync();

    /// <summary>The timer's tick skips a beat while the last one is still running; everyone else waits for it.</summary>
    internal async Task TickAsync(bool skipIfBusy = false)
    {
        if (skipIfBusy)
        {
            if (!_tickGate.Wait(0))
            {
                return;
            }
        }
        else
        {
            await _tickGate.WaitAsync();
        }

        try
        {
            await TickCoreAsync();
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task TickCoreAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            Running = null;
            return;
        }

        try
        {
            var (found, memory) = await OffUiThread(() =>
            {
                var running = _processes.FindRunning(profile);
                return (running, running is null ? null : _processes.GetMemoryBytes(running.ProcessId));
            });
            if (SelectedProfile?.Id != profile.Id)
            {
                return; // another server was selected while this looked
            }

            _memoryBytes = memory;
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

        if (_shownWatch is { } watch && watch.Profile.Id == profile.Id)
        {
            await PollWatchAsync(watch);
            ExpireStalePlayers(watch);
        }

        RefreshOverview();
    }

    // ---- Log ----

    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty] private bool _hideNoise = true;
    [ObservableProperty] private bool _followLog = true;
    [ObservableProperty] private string _logFilter = "";

    public string LogPath => SelectedProfile is null ? "" : DedicatedServerLayout.LogPath(SelectedProfile.InstallFolder, SelectedProfile.SavedDirSuffix, SelectedProfile.LogFileName);

    partial void OnHideNoiseChanged(bool value) => RebuildVisibleLog();
    partial void OnLogFilterChanged(string value) => RebuildVisibleLog();

    /// <summary>Turning Follow back on jumps to the newest line instead of waiting for the next one.</summary>
    partial void OnFollowLogChanged(bool value) => ScrollLogIfFollowing();

    private void ScrollLogIfFollowing()
    {
        if (FollowLog && LogLines.Count > 0)
        {
            LogAppended?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised after new lines are shown and FollowLog is on — the view scrolls to the end.</summary>
    public event EventHandler? LogAppended;

    [RelayCommand]
    private void ClearLog()
    {
        _allLogLines.Clear();
        LogLines.Clear();
    }

    /// <summary>Puts lines in the Log panel (the server's own lines come from its watch, already read and fed to the players list).</summary>
    private void AppendLog(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // The server logs the join password in plain text on every login — never show or keep it.
        lines = [.. lines.Select(ServerLogFilter.Redact)];
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

        ScrollLogIfFollowing();
    }

    private bool IsVisible(string line) =>
        line.Length > 0
        && (!HideNoise || !ServerLogFilter.IsNoise(line))
        && (string.IsNullOrWhiteSpace(LogFilter) || line.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase));
}
