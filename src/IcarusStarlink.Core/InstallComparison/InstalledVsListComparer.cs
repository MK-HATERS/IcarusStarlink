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

public sealed record InstalledComparisonEntry(string Name, InstalledComparisonState State, bool IsUe4ss);

/// <summary>
/// "Installed vs this list" — a preview of what clicking Install would actually change, comparing
/// what this app can positively identify as currently installed (InstallService.GetInstalledStateAsync,
/// read from its own manifests) against the current profile's own queue and attached UE4SS set.
/// Pure comparison, no I/O — the caller resolves both sides first.
/// </summary>
public static class InstalledVsListComparer
{
    public static IReadOnlyList<InstalledComparisonEntry> Compare(
        IReadOnlyList<string> installedModNames, IReadOnlyList<string> queuedModNames,
        IReadOnlyList<string> installedUe4ssModNames, IReadOnlyList<string> attachedUe4ssModNames)
    {
        var entries = new List<InstalledComparisonEntry>();
        entries.AddRange(CompareSet(installedModNames, queuedModNames, isUe4ss: false));
        entries.AddRange(CompareSet(installedUe4ssModNames, attachedUe4ssModNames, isUe4ss: true));
        return entries;
    }

    private static IEnumerable<InstalledComparisonEntry> CompareSet(IReadOnlyList<string> installed, IReadOnlyList<string> desired, bool isUe4ss)
    {
        var installedSet = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        var desiredSet = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);

        foreach (var name in desired.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new InstalledComparisonEntry(
                name, installedSet.Contains(name) ? InstalledComparisonState.Unchanged : InstalledComparisonState.Added, isUe4ss);
        }

        foreach (var name in installed.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!desiredSet.Contains(name))
            {
                yield return new InstalledComparisonEntry(name, InstalledComparisonState.Removed, isUe4ss);
            }
        }
    }
}
