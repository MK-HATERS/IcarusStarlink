namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>Holds the single latest WeeklyChangeReport — overwritten on every "Update data folder" run that had a previous extraction to compare against, matching classic IMM's own "This Weeks Changes" being a single current entry rather than an accumulating history.</summary>
public interface IWeeklyChangeReportStore
{
    WeeklyChangeReport? Current { get; }

    void Save(WeeklyChangeReport report);
}
