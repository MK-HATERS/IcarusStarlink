using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Phase 3: who is online and what happened, read from the server's own log (there is no network RCON for Icarus — the log is the only window in).</summary>
public sealed partial class LocalServerViewModel
{
    private const int MaxEvents = 200;

    private readonly ServerLogEvents _logEvents = new();

    public ObservableCollection<OnlinePlayer> OnlinePlayers { get; } = [];

    /// <summary>Newest first: "20:11  Prospect loaded: Hard Elysium JULY (Elysium)".</summary>
    public ObservableCollection<string> Events { get; } = [];

    [ObservableProperty]
    private string? _currentProspect;

    public string OnlineSummary => OnlinePlayers.Count switch
    {
        0 => "Nobody online",
        1 => "1 player online",
        var n => $"{n} players online",
    };

    private void ResetPlayersAndEvents()
    {
        _logEvents.Reset();
        OnlinePlayers.Clear();
        Events.Clear();
        CurrentProspect = null;
        OnPropertyChanged(nameof(OnlineSummary));
    }

    private void FeedPlayersAndEvents(IEnumerable<string> lines)
    {
        var changed = false;
        foreach (var line in lines)
        {
            if (!line.StartsWith("[SteamCMD]", StringComparison.Ordinal))
            {
                changed |= AddEvents(_logEvents.Feed(line));
            }
        }

        if (changed)
        {
            RefreshOnlinePlayers();
        }
    }

    /// <summary>A quiet server writes nothing — the timer still turns an unanswered disconnect into "left" after the grace period.</summary>
    private void ExpireStalePlayers()
    {
        if (AddEvents(_logEvents.ExpireStale(DateTime.UtcNow)))
        {
            RefreshOnlinePlayers();
        }
    }

    /// <summary>Error lines stay in the log itself (a normal start logs dozens of harmless asset errors); the Events list keeps to what an owner acts on.</summary>
    private bool AddEvents(IReadOnlyList<ServerLogEvent> events)
    {
        var changed = false;
        foreach (var ev in events.Where(e => e.Kind != ServerLogEventKind.Error))
        {
            changed = true;
            var time = ev.Time?.ToLocalTime().ToString("HH:mm") ?? "--:--";
            Events.Insert(0, $"{time}  {ev.Text}");
            while (Events.Count > MaxEvents)
            {
                Events.RemoveAt(Events.Count - 1);
            }
        }

        return changed || events.Count > 0;
    }

    private void RefreshOnlinePlayers()
    {
        CurrentProspect = _logEvents.CurrentProspect;
        OnlinePlayers.Clear();
        foreach (var player in _logEvents.Online.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            OnlinePlayers.Add(player);
        }

        OnPropertyChanged(nameof(OnlineSummary));
    }
}
