namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Which game version a mod was made against, read from the game rows it carries.
/// <paramref name="Earliest"/>/<paramref name="Latest"/> are history positions: every row the mod
/// carries existed from Earliest on (the newest one appeared then) and until Latest (the first one
/// the game removed went after it). Latest &lt; the history's newest means the mod is older than the game.
/// Earliest after Latest is a mod updated piecemeal: built for Earliest or later, but still
/// carrying <paramref name="RemovedRows"/> rows the game removed after Latest.
/// </summary>
public sealed record ModGameVersion(int Earliest, int Latest, string EarliestLabel, string LatestLabel, bool OlderThanGame, int RowsCompared, int RemovedRows = 0)
{
    /// <summary>"Made for the game between Week 222 and 4 Sep 2026", or null when the mod fits today's game.</summary>
    public string? OlderText => !OlderThanGame
        ? null
        : Earliest > Latest
            ? $"Made for {EarliestLabel} or later, but still edits {RemovedRows} row(s) the game removed after {LatestLabel}"
            : Earliest > 0 && EarliestLabel != LatestLabel
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
        var removed = 0;
        foreach (var row in package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)))
        {
            foreach (var item in row.FileItems.DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                // Rows the game never had are the mod's own: they say nothing about its game version.
                if (history.Find(row.CurrentFile, item.Name) is not { } seen)
                {
                    continue;
                }

                rows++;
                earliest = Math.Max(earliest, seen.First);
                latest = Math.Min(latest, seen.Last);
                if (seen.Last < history.Latest)
                {
                    removed++;
                }
            }
        }

        if (rows < MinRows)
        {
            return null;
        }

        // A mod updated piecemeal can carry rows from two game versions (earliest after latest):
        // made for the newer one, still carrying rows the game removed before it.
        return new ModGameVersion(earliest, latest, history.LabelOf(earliest), history.LabelOf(latest), latest < history.Latest, rows, removed);
    }
}
