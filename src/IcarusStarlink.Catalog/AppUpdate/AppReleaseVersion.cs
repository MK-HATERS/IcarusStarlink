using System.Text.RegularExpressions;

namespace IcarusStarlink.Catalog.AppUpdate;

/// <summary>
/// A Starlink version as releases are tagged: "2.9.1" (stable) or "2.10.0-beta.1" (beta, a GitHub
/// prerelease). System.Version can't read the second, so the update check couldn't compare a beta
/// at all. Order: numbers first; a beta comes before the stable release of the same numbers
/// (2.10.0-beta.2 &lt; 2.10.0), and betas order by their number (beta.1 &lt; beta.2).
/// Anything after "+" (build metadata) is ignored.
/// </summary>
public sealed partial record AppReleaseVersion(Version Numbers, string? Prerelease) : IComparable<AppReleaseVersion>
{
    [GeneratedRegex(@"^v?(\d+(?:\.\d+){1,3})(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$")]
    private static partial Regex Pattern();

    public bool IsPrerelease => Prerelease is not null;

    public static AppReleaseVersion? TryParse(string? text)
    {
        var match = Pattern().Match(text?.Trim() ?? "");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var numbers)
            ? new AppReleaseVersion(Normalize(numbers), match.Groups[2].Success ? match.Groups[2].Value : null)
            : null;
    }

    /// <summary>"2.9" and "2.9.0" are the same release.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public int CompareTo(AppReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byNumbers = Numbers.CompareTo(other.Numbers);
        if (byNumbers != 0)
        {
            return byNumbers;
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => ComparePrerelease(Prerelease, other.Prerelease),
        };
    }

    /// <summary>Dot-separated parts left to right; number parts compare as numbers ("beta.10" after "beta.9").</summary>
    private static int ComparePrerelease(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var bothNumbers = int.TryParse(left[i], out var x) & int.TryParse(right[i], out var y);
            var order = bothNumbers ? x.CompareTo(y) : string.CompareOrdinal(left[i], right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator >(AppReleaseVersion a, AppReleaseVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(AppReleaseVersion a, AppReleaseVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => Prerelease is null ? $"{Numbers.Major}.{Numbers.Minor}.{Numbers.Build}" : $"{Numbers.Major}.{Numbers.Minor}.{Numbers.Build}-{Prerelease}";
}
