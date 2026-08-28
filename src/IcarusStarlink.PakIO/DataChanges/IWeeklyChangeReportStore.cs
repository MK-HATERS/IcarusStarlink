namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// Keeps roughly a month of WeeklyChangeReport history — one per "Update data folder" run that had
/// a previous extraction to compare against — rather than the single overwritten entry this
/// started as. Real cost of the old design: a live game patch's real diff was silently gone the
/// moment anyone re-ran the update again for any reason (confirmed the hard way — a same-day
/// re-run for an unrelated check erased a real patch's own tracked changes). Anything older than
/// RetentionDays is pruned on the next Save, not proactively — a report that's still within the
/// window is never deleted just for being read.
/// </summary>
public interface IWeeklyChangeReportStore
{
    /// <summary>The most recent report, or null if none have ever been saved (or none are left in the retention window).</summary>
    WeeklyChangeReport? Current { get; }

    /// <summary>Every retained report, newest first — includes Current as its own first entry.</summary>
    IReadOnlyList<WeeklyChangeReport> History { get; }

    void Save(WeeklyChangeReport report);
}
