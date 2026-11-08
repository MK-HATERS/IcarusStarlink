using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Who is online and what happened, read from the server's own log (there is no network
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

        RefreshRecentEvents();
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

            RefreshRecentEvents();
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

    // ---- Steam64 ids: copy, or put into the admin / ban list boxes (Save lists writes them) ----

    private static bool PlayerHasSteam64Id(OnlinePlayer? player) => player?.Steam64Id is not null;

    [RelayCommand(CanExecute = nameof(PlayerHasSteam64Id))]
    private void CopyPlayerId(OnlinePlayer? player)
    {
        if (player?.Steam64Id is { } id)
        {
            CopyText(id, $"Copied {player.Name}'s Steam64 id ({id}).");
        }
    }

    [RelayCommand(CanExecute = nameof(PlayerHasSteam64Id))]
    private void AddPlayerToAdmins(OnlinePlayer? player)
    {
        if (player?.Steam64Id is { } id)
        {
            AdminWhitelistText = AddIdToList(AdminWhitelistText, id, player.Name, "admin list");
        }
    }

    [RelayCommand(CanExecute = nameof(PlayerHasSteam64Id))]
    private void BanPlayer(OnlinePlayer? player)
    {
        if (player?.Steam64Id is { } id)
        {
            BanListText = AddIdToList(BanListText, id, player.Name, "ban list");
        }
    }

    /// <summary>Adds the id as its own line unless it is already there, and says that Save lists (server stopped) makes it count.</summary>
    private string AddIdToList(string? text, string id, string name, string label)
    {
        text ??= "";
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim());
        if (lines.Contains(id, StringComparer.Ordinal))
        {
            StatusMessage = $"{name} is already on the {label}.";
            return text;
        }

        StatusMessage = $"Added {name} to the {label} on the Admins & bans tab, not saved yet. Click Save lists there while the server is stopped to make it count.";
        var start = text.Length == 0 || text.EndsWith('\n') ? text : text + "\r\n";
        return $"{start}{id}\r\n";
    }
}
