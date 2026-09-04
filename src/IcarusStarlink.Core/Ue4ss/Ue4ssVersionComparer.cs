using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Verdict from comparing a mod's user-declared Ue4ssModMeta.MinUe4ssVersion against the installed
/// loader's own Ue4ssLoaderStatus.InstalledVersion. Deliberately three-valued rather than a plain
/// bool: either string can be missing, or not a clean dotted-numeric version this can confidently
/// compare (Ue4ssLogVersionParser's own capture is a raw non-whitespace token straight off UE4SS.log
/// — e.g. a build tagged "3.0.1-beta" with no space before the tag) — and guessing at a version
/// buried in a suffix is worse than just admitting the check couldn't run.
/// </summary>
public enum Ue4ssVersionCompatibility
{
    /// <summary>No declared minimum, no installed version, or either string isn't a clean dotted-numeric version — never surfaced as a warning.</summary>
    Unknown,

    /// <summary>The installed loader version is greater than or equal to the mod's declared minimum.</summary>
    Met,

    /// <summary>The installed loader version is below the mod's declared minimum — the only verdict a UI should warn on.</summary>
    BelowMinimum,
}

/// <summary>
/// This is specifically and only a manual, user-declared minimum-version check against the
/// already-tracked installed loader version — not mod conflict detection, and not automatic
/// discovery of a mod's real requirement (there is nothing on disk to discover it from; see
/// Ue4ssModMeta's own doc comment). Numeric-only: "3.0.9" vs "3.0.10" compares component-by-component
/// like a real version (unlike a plain ordinal string compare, where "3.0.10" sorts before "3.0.9"),
/// and anything that isn't a clean dotted-numeric string on either side degrades to Unknown rather
/// than guess-parsing a version out of a suffix.
/// </summary>
public static partial class Ue4ssVersionComparer
{
    [GeneratedRegex(@"^\d+(\.\d+)*$")]
    private static partial Regex CleanDottedNumericPattern();

    /// <summary>Never throws — anything it can't confidently parse as a version on either side just yields Unknown.</summary>
    public static Ue4ssVersionCompatibility Compare(string? minUe4ssVersion, string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(minUe4ssVersion) || string.IsNullOrWhiteSpace(installedVersion))
        {
            return Ue4ssVersionCompatibility.Unknown;
        }

        if (!TryParseCleanDottedNumeric(minUe4ssVersion, out var required) ||
            !TryParseCleanDottedNumeric(installedVersion, out var installed))
        {
            return Ue4ssVersionCompatibility.Unknown;
        }

        return installed < required ? Ue4ssVersionCompatibility.BelowMinimum : Ue4ssVersionCompatibility.Met;
    }

    /// <summary>
    /// Only a string that is ENTIRELY digits and dots (after trimming) counts — "3.0.1-beta" is
    /// rejected outright rather than stripping the suffix and parsing "3.0.1" out of it, per this
    /// type's own "don't guess" contract.
    /// </summary>
    private static bool TryParseCleanDottedNumeric(string value, out Version version)
    {
        var trimmed = value.Trim();
        if (!CleanDottedNumericPattern().IsMatch(trimmed))
        {
            version = new Version();
            return false;
        }

        // System.Version needs at least major.minor — a bare "3" is a clean dotted-numeric string by
        // this method's own definition but Version.TryParse rejects it with no dot at all, so pad it
        // rather than let a single-component version incorrectly fall back to Unknown.
        var normalized = trimmed.Contains('.') ? trimmed : $"{trimmed}.0";
        return Version.TryParse(normalized, out version!);
    }
}
