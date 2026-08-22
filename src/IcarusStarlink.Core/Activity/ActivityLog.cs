using System.Collections.ObjectModel;

namespace IcarusStarlink.Core.Activity;

public enum ActivityKind { Info, Success, Warning }

public sealed record ActivityEntry(string Message, ActivityKind Kind, DateTimeOffset Timestamp);

/// <summary>
/// Phase 10: a cross-cutting, app-wide feed of recent actions — Rebuild/Install/import/save
/// completions and the like — surfaced in a persistent panel that stays available across page
/// navigation, unlike each page's own status line, which disappears the moment you navigate away.
/// </summary>
public interface IActivityLog
{
    /// <summary>Newest entry first. Capped — see MaxEntries.</summary>
    ObservableCollection<ActivityEntry> Entries { get; }

    void Log(string message, ActivityKind kind = ActivityKind.Info);
}

/// <summary>In-memory only — this session's own activity, not persisted across restarts.</summary>
public sealed class ActivityLog : IActivityLog
{
    private const int MaxEntries = 50;

    public ObservableCollection<ActivityEntry> Entries { get; } = [];

    public void Log(string message, ActivityKind kind = ActivityKind.Info)
    {
        Entries.Insert(0, new ActivityEntry(message, kind, DateTimeOffset.Now));
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }
    }
}
