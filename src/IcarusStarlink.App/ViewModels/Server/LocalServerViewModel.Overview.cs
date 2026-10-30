using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The Overview tab's tiles for the selected server: players, world, uptime, memory, build, backups, and what automation does next.</summary>
public sealed partial class LocalServerViewModel
{
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

    /// <summary>Automation restarts this server on its own while 'Save on exit' is off: each restart loses progress since the last autosave.</summary>
    [ObservableProperty] private string? _saveOnExitWarning;

    partial void OnSaveGameOnExitChanged(bool? value) => RefreshOverview();

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
            MemoryTile = _memoryBytes is { } b ? $"{b / (1024.0 * 1024 * 1024):0.0} GB" : "-";
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
        BuildHint = InstalledBuildId is null ? "build not known" : $"build {InstalledBuildId} · {InstalledBranch ?? "public"}";

        var newest = Backups.OrderByDescending(b => b.CreatedLocal).FirstOrDefault();
        BackupTile = newest is null ? "None" : newest.CreatedLocal.Date == DateTime.Today ? newest.CreatedLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : newest.CreatedLocal.ToString("d MMM", CultureInfo.CurrentCulture);
        BackupHint = $"{Backups.Count} kept";

        // The form's values: what the Settings tab shows (saved or about to be).
        var saveFrequency = double.TryParse(GameSaveFrequencyText, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : (double?)null;
        SaveOnExitWarning = ServerShutdownNotes.AutomationWarning(p, new DedicatedServerSettings { SaveGameOnExit = SaveGameOnExit, GameSaveFrequency = saveFrequency });

        // Coming up: ServerAutomation's own state (what it waits for, when it next looks), for the options that are on.
        // What automation last did is in Recent events, with everything else that happened.
        var watch = _watches.GetValueOrDefault(p.Id);
        var players = watch?.Events.Online.Count ?? OnlinePlayers.Count;
        var lines = (watch?.Automation ?? new ServerAutomation()).DescribeUpcoming(p, Clock(), players).ToList();
        Replace(NextUp, lines);
        RefreshRecentEvents();
        OnPropertyChanged(nameof(NowLine));
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> lines)
    {
        if (target.SequenceEqual(lines))
        {
            return;
        }

        target.Clear();
        foreach (var line in lines)
        {
            target.Add(line);
        }
    }

    // ---- Recent events: the newest few of the Log tab's Events list ----

    public const int MaxRecentEvents = 8;

    /// <summary>The newest MaxRecentEvents of the Events list (newest first, local time), without the start-up's "Mod pak loaded" lines.</summary>
    public ObservableCollection<string> RecentEvents { get; } = [];

    public bool HasRecentEvents => RecentEvents.Count > 0;

    private void RefreshRecentEvents()
    {
        Replace(RecentEvents, Events.Where(e => !e.Contains(ServerLogEvents.ModPakMountedText, StringComparison.Ordinal)).Take(MaxRecentEvents).ToList());
        OnPropertyChanged(nameof(HasRecentEvents));
    }

    // ---- Now: one plain sentence for what the server is doing ----

    /// <summary>How long after its start a server with a world to load is still called "starting".</summary>
    private static readonly TimeSpan WorldLoadGrace = TimeSpan.FromMinutes(3);

    /// <summary>"Running · world 'Olympus' · 2 players · up 3h 12m", "Updating with SteamCMD (downloading 40%)", "Stopped · last stopped cleanly at 14:02".</summary>
    public string NowLine
    {
        get
        {
            if (SelectedProfile is not { } p)
            {
                return "";
            }

            var watch = _watches.GetValueOrDefault(p.Id);
            if (IsSteamCmdBusy && _steamCmdFor == p.Id)
            {
                return _steamCmdDownloads
                    ? $"Updating with SteamCMD ({SteamCmdStatus ?? "starting"})"
                    : "Asking Steam whether a newer server build is out…";
            }

            if (watch is { AutomationBusy: true })
            {
                return $"Automation: {watch.AutomationDoing ?? "restarting it"}…";
            }

            if (IsBusy && _busyWith == "UPDATING")
            {
                return "Updating: backing up its worlds and settings, then SteamCMD downloads the new build…";
            }

            if (IsBusy && Running is null)
            {
                return "Starting… (waiting for the server to open)";
            }

            if (IsBusy)
            {
                return "Stopping… (waiting up to 60 seconds for it to close)";
            }

            var automation = watch?.Automation;
            if (Running is { } r)
            {
                var up = DateTime.Now - r.StartTime;
                var prospect = watch?.Events.CurrentProspect ?? CurrentProspect;
                if (prospect is null && up < WorldLoadGrace && ExpectsAWorld())
                {
                    return "Starting… (loading the world)";
                }

                var players = watch?.Events.Online.Count ?? OnlinePlayers.Count;
                var parts = new List<string>
                {
                    "Running",
                    prospect is null ? "in the lobby" : $"world '{prospect}'",
                    players switch { 0 => "nobody online", 1 => "1 player", _ => $"{players} players" },
                    $"up {FormatUptime(up)}",
                };
                if (automation is { LastStopKind: ServerStopKind.Crash, LastStoppedAt: { } crashed }
                    && r.StartTime >= crashed.AddMinutes(-1) && Clock() - crashed < TimeSpan.FromDays(1))
                {
                    parts.Add($"restarted after a crash at {At(crashed)}");
                }

                return string.Join(" · ", parts);
            }

            if (automation?.LastStoppedAt is not { } stopped)
            {
                return "Stopped";
            }

            return automation.LastStopKind switch
            {
                ServerStopKind.ByOwner => $"Stopped · you stopped it at {At(stopped)}",
                ServerStopKind.Clean => $"Stopped · last stopped cleanly at {At(stopped)}",
                _ when automation.GaveUp => $"Stopped · it crashed at {At(stopped)}, too often to keep restarting it. Check the Log tab, then Start it.",
                _ when !p.AutoRestartOnCrash => $"Stopped · it crashed at {At(stopped)} ('Restart it if it crashes' is off)",
                _ => $"Stopped · it crashed at {At(stopped)}; starting it again",
            };
        }
    }

    /// <summary>The server loads a world on start (rather than waiting in the lobby): resume the last one, or load / create one.</summary>
    private bool ExpectsAWorld() =>
        (ResumeProspect != false && !string.IsNullOrWhiteSpace(LastProspectName))
        || !string.IsNullOrWhiteSpace(LoadProspect) || !string.IsNullOrWhiteSpace(CreateProspect);

    /// <summary>"14:02" today, "29 Oct 14:02" another day.</summary>
    private string At(DateTime local) =>
        local.Date == Clock().Date ? local.ToString("HH:mm", CultureInfo.CurrentCulture) : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
}
