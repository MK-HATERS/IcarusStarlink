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
        BuildHint = InstalledBuildId is null ? "no Steam manifest" : $"build {InstalledBuildId} · {InstalledBranch ?? "public"}";

        var newest = Backups.OrderByDescending(b => b.CreatedLocal).FirstOrDefault();
        BackupTile = newest is null ? "None" : newest.CreatedLocal.Date == DateTime.Today ? newest.CreatedLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : newest.CreatedLocal.ToString("d MMM", CultureInfo.CurrentCulture);
        BackupHint = $"{Backups.Count} kept";

        // The form's values: what the Settings tab shows (saved or about to be).
        var saveFrequency = double.TryParse(GameSaveFrequencyText, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : (double?)null;
        SaveOnExitWarning = ServerShutdownNotes.AutomationWarning(p, new DedicatedServerSettings { SaveGameOnExit = SaveGameOnExit, GameSaveFrequency = saveFrequency });

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
}
