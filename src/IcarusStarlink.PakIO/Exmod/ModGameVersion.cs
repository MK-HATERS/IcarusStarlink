namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Which game version a mod was made against, read from the game rows it carries.
/// <paramref name="Earliest"/>/<paramref name="Latest"/> are history positions: every row the mod
/// carries existed from Earliest on (the newest one appeared then) and until Latest (the first one
/// the game removed went after it). Latest &lt; the history's newest means the mod is older than the game.
/// </summary>
public sealed record ModGameVersion(int Earliest, int Latest, string EarliestLabel, string LatestLabel, bool OlderThanGame, int RowsCompared)
{
    /// <summary>"Made for the game between Week 222 and 4 Sep 2026", or null when the mod fits today's game.</summary>
    public string? OlderText => !OlderThanGame
        ? null
        : Earliest > 0 && Earliest <= Latest && EarliestLabel != LatestLabel
            ? $"Made for an older game: between {EarliestLabel} and {LatestLabel}"
            : $"Made for an older game: {LatestLabel} or earlier";
}

/// <summary>Estimates <see cref="ModGameVersion"/> from a mod's rows and the game's row history.</summary>
public static class ModGameVersionEstimator
{
    /// <summary>Fewer known game rows than this tells too little (a small edit to a long-lived row).</summary>
    private const int MinRows = 5;

    public static ModGameVersion? Estimate(ExmodPackage package, GameRowHistory history)
    {
        if (history.VersionCount == 0)
        {
            return null;
        }

        var earliest = 0;
        var latest = history.Latest;
        var rows = 0;
        foreach (var row in package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)))
        {
            foreach (var item in row.FileItems)
            {
                // Rows the game never had are the mod's own: they say nothing about its game version.
                if (history.Find(row.CurrentFile, item.Name) is not { } seen)
                {
                    continue;
                }

                rows++;
                earliest = Math.Max(earliest, seen.First);
                latest = Math.Min(latest, seen.Last);
            }
        }

        if (rows < MinRows)
        {
            return null;
        }

        // A mod updated piecemeal can carry rows from two game versions (earliest after latest):
        // the removed rows still prove it's older; when it was made can't be narrowed further.
        if (earliest > latest)
        {
            earliest = 0;
        }

        return new ModGameVersion(earliest, latest, history.LabelOf(earliest), history.LabelOf(latest), latest < history.Latest, rows);
    }
}
