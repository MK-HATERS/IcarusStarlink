namespace IcarusStarlink.Updater;

/// <summary>
/// The update-outcome note's names, shared word for word by the updater (UpdateOutcomeFile writes
/// it) and the app (AppUpdateOutcome reads it): IcarusStarlink.App.csproj compiles this same file.
/// </summary>
internal static class UpdateOutcomeNote
{
    public const string FileName = "update-outcome.txt";
    public const string TempFileName = "IcarusStarlink-update-outcome.txt";

    /// <summary>Line 1 of a good update's note: UpdateRunner.Outcome.Applied's name (a test pins the two together).</summary>
    public const string AppliedLine = "Applied";
}
