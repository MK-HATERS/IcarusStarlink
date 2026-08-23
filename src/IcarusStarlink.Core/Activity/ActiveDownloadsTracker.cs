using System.Collections.ObjectModel;

namespace IcarusStarlink.Core.Activity;

/// <summary>One download in flight — Key identifies it (ModId:FileId) so Start/Finish always target the right entry even if two downloads overlap; DisplayName is the real mod name when the caller already knows it (a Nexus card), else a "mod #N" fallback.</summary>
public sealed record ActiveDownloadEntry(string Key, string DisplayName);

/// <summary>
/// Cross-cutting, app-wide record of downloads currently in progress — lets the Library page show
/// a live "downloading…" stub for a mod that isn't in the Library yet without Library needing to
/// know anything about how Nexus downloads work. Same in-memory, session-only shape as IActivityLog.
/// </summary>
public interface IActiveDownloadsTracker
{
    /// <summary>Whatever's currently downloading — empty once every in-flight download finishes or fails.</summary>
    ObservableCollection<ActiveDownloadEntry> Current { get; }

    void Start(string key, string displayName);

    /// <summary>Always call this when a download ends, success or failure — a stub that never clears would be worse than no stub at all.</summary>
    void Finish(string key);
}

public sealed class ActiveDownloadsTracker : IActiveDownloadsTracker
{
    public ObservableCollection<ActiveDownloadEntry> Current { get; } = [];

    public void Start(string key, string displayName)
    {
        var existing = Current.FirstOrDefault(d => d.Key == key);
        if (existing is not null)
        {
            Current.Remove(existing);
        }

        Current.Add(new ActiveDownloadEntry(key, displayName));
    }

    public void Finish(string key)
    {
        var existing = Current.FirstOrDefault(d => d.Key == key);
        if (existing is not null)
        {
            Current.Remove(existing);
        }
    }
}
