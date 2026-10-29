using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 3: who is online and what happened, read from the server's own log (there is no network
/// RCON for Icarus — the log is the only window in). Every server's log is read once, by its
/// watch (see the .Automation partial); the panels here show the selected server's watch.
/// </summary>
public sealed partial class LocalServerViewModel
{
    private const int MaxEvents = 200;

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

    /// <summary>Fills the Players and Events panels from a watch (null clears them).</summary>
    private void ShowPlayersAndEventsOf(ServerWatch? watch)
    {
        Events.Clear();
        foreach (var line in watch?.EventLines ?? [])
        {
            Events.Add(line);
        }

        RefreshOnlinePlayers(watch);
    }

    /// <summary>Error lines stay in the log itself (a normal start logs dozens of harmless asset errors); the Events list keeps to what an owner acts on. True when anything happened.</summary>
    private bool RecordEvents(ServerWatch watch, IReadOnlyList<ServerLogEvent> events)
    {
        foreach (var ev in events.Where(e => e.Kind != ServerLogEventKind.Error))
        {
            RecordEventLine(watch, $"{ev.Time?.ToLocalTime().ToString("HH:mm") ?? "--:--"}  {ev.Text}");
        }

        return events.Count > 0;
    }

    private void RecordEventLine(ServerWatch watch, string text)
    {
        watch.EventLines.Insert(0, text);
        if (watch.EventLines.Count > MaxEvents)
        {
            watch.EventLines.RemoveRange(MaxEvents, watch.EventLines.Count - MaxEvents);
        }

        if (ReferenceEquals(_shownWatch, watch))
        {
            Events.Insert(0, text);
            while (Events.Count > MaxEvents)
            {
                Events.RemoveAt(Events.Count - 1);
            }
        }
    }

    /// <summary>A quiet server writes nothing — the timer still turns an unanswered disconnect into "left" after the grace period.</summary>
    private void ExpireStalePlayers(ServerWatch watch)
    {
        if (RecordEvents(watch, watch.Events.ExpireStale(DateTime.UtcNow)) && ReferenceEquals(_shownWatch, watch))
        {
            RefreshOnlinePlayers(watch);
        }
    }

    /// <summary>The server stopped or a new run began: nobody is online any more.</summary>
    private void ResetWatchEvents(ServerWatch watch)
    {
        watch.Events.Reset();
        if (ReferenceEquals(_shownWatch, watch))
        {
            RefreshOnlinePlayers(watch);
        }
    }

    private void RefreshOnlinePlayers(ServerWatch? watch)
    {
        CurrentProspect = watch?.Events.CurrentProspect;
        OnlinePlayers.Clear();
        foreach (var player in (watch?.Events.Online ?? []).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            OnlinePlayers.Add(player);
        }

        OnPropertyChanged(nameof(OnlineSummary));
    }
}
