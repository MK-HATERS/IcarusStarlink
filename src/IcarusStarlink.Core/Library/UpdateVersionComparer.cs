using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Library;

/// <summary>
/// The one place that decides whether a mod's fetched "latest" version string means an update is
/// actually available — shared by Library's Mods tab, its UE4SS tab, and the Nexus catalog's own
/// "In Library" rows, which used to each do a plain ordinal string compare. A real bug found live:
/// a Nexus EXMODZ whose author wrote "1.0" inside the EXMOD while the Nexus page says "1" showed
/// "Update available: v1" forever, with nothing to update to. Version strings from the two sides
/// are written by different people in different places (the mod's own metadata vs a Nexus/catalog
/// form field), so "1" / "1.0" / "v1.0.0" all have to count as the same version.
/// </summary>
public static partial class UpdateVersionComparer
{
    [GeneratedRegex(@"^[vV]?\d+(\.\d+)*$")]
    private static partial Regex DottedNumericPattern();

    /// <summary>
    /// True when the two strings name the same version: equal ignoring case and surrounding
    /// whitespace, or both clean dotted-numeric (an optional leading "v" allowed) and equal
    /// component-by-component with trailing zero components ignored ("1" == "1.0" == "v1.0.0",
    /// "1.1" != "1.10"). Anything that isn't numeric on BOTH sides falls back to the plain text
    /// compare — "2.0-beta" vs "2.0" is genuinely different, and guessing otherwise would hide a
    /// real update. Null/empty never equals anything, including another null.
    /// </summary>
    public static bool AreEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var leftTrimmed = left.Trim();
        var rightTrimmed = right.Trim();
        if (string.Equals(leftTrimmed, rightTrimmed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!DottedNumericPattern().IsMatch(leftTrimmed) || !DottedNumericPattern().IsMatch(rightTrimmed))
        {
            return false;
        }

        return NormalizeComponents(leftTrimmed).SequenceEqual(NormalizeComponents(rightTrimmed));
    }

    /// <summary>
    /// The actual "is there an update?" rule. <paramref name="latestVersion"/> is what the source
    /// (Nexus, the catalog) reports right now; <paramref name="installedVersion"/> is the version
    /// the mod declares about itself; <paramref name="recordedSourceVersion"/> is what the SOURCE
    /// said the version was at the moment this copy was downloaded or linked (null when that was
    /// never recorded, e.g. an older import). No update when the latest matches EITHER of the
    /// installed-side strings: the recorded source version is the authoritative "this is the file
    /// you got" marker when it exists, and the declared version covers everything imported before
    /// it was recorded. An unknown latest never counts as an update.
    /// </summary>
    public static bool HasUpdate(string? latestVersion, string? installedVersion, string? recordedSourceVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return false;
        }

        if (AreEquivalent(latestVersion, recordedSourceVersion))
        {
            return false;
        }

        if (AreEquivalent(latestVersion, installedVersion))
        {
            return false;
        }

        // Neither side known at all — nothing to compare against, so nothing to claim.
        return !string.IsNullOrWhiteSpace(installedVersion) || !string.IsNullOrWhiteSpace(recordedSourceVersion);
    }

    private static IReadOnlyList<int> NormalizeComponents(string version)
    {
        var components = version.TrimStart('v', 'V').Split('.').Select(int.Parse).ToList();
        while (components.Count > 1 && components[^1] == 0)
        {
            components.RemoveAt(components.Count - 1);
        }

        return components;
    }
}
