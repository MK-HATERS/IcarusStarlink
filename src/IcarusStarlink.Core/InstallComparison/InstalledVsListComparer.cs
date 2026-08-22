namespace IcarusStarlink.Core.InstallComparison;

public enum InstalledComparisonState
{
    /// <summary>In the current list, not currently installed — would be added by Install.</summary>
    Added,

    /// <summary>Currently installed, not in the current list — would be removed by Install.</summary>
    Removed,

    /// <summary>In both — Install wouldn't change this one.</summary>
    Unchanged,
}

public sealed record InstalledComparisonEntry(string Name, InstalledComparisonState State);

/// <summary>
/// "Installed vs this list" — a preview of what clicking Install would actually change, comparing
/// what this app can positively identify as currently installed (InstallService.GetInstalledStateAsync,
/// read from its own pak manifest) against the current profile's own queue. Pure comparison, no I/O
/// — the caller resolves both sides first. UE4SS mods aren't part of this comparison (Phase 8.5) —
/// they're enabled/disabled directly, not staged against an intended "list" the way pak mods are.
/// </summary>
public static class InstalledVsListComparer
{
    public static IReadOnlyList<InstalledComparisonEntry> Compare(IReadOnlyList<string> installedModNames, IReadOnlyList<string> queuedModNames)
    {
        var installedSet = new HashSet<string>(installedModNames, StringComparer.OrdinalIgnoreCase);
        var desiredSet = new HashSet<string>(queuedModNames, StringComparer.OrdinalIgnoreCase);

        var entries = new List<InstalledComparisonEntry>();
        foreach (var name in queuedModNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(new InstalledComparisonEntry(
                name, installedSet.Contains(name) ? InstalledComparisonState.Unchanged : InstalledComparisonState.Added));
        }

        foreach (var name in installedModNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!desiredSet.Contains(name))
            {
                entries.Add(new InstalledComparisonEntry(name, InstalledComparisonState.Removed));
            }
        }

        return entries;
    }
}
