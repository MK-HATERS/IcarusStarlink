using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Which UE4SS build something is. UE4SS.log names only the version of the last official release
/// ("v3.0.1", "v4.0.0"): the release itself and every dev build after it say the same, so the
/// version alone can't tell them apart. The git commit can. Official releases are known here by
/// their tag's commit; dev builds come from GitHub's rolling "experimental-latest" release, named
/// like "UE4SS_v4.0.0-6-gef15eba8.zip" (6 commits after the v4.0.0 tag, commit ef15eba8).
/// </summary>
public static partial class Ue4ssBuild
{
    /// <summary>The commit of the official v3.0.1 release tag (UE4SS-RE/RE-UE4SS).</summary>
    public const string OfficialReleaseCommit = "d935b5b23bac03b65c14ae38382b02007204cc2e";

    /// <summary>The commit of the official v4.0.0 release tag.</summary>
    public const string Official400Commit = "30afe5fff94a94c2313e220b2002059a080da9a0";

    /// <summary>Every official release Starlink knows, oldest first. Add a line when UE4SS tags a new one.</summary>
    private static readonly (string Version, string Commit)[] OfficialReleases =
    [
        ("3.0.1", OfficialReleaseCommit),
        ("4.0.0", Official400Commit),
    ];

    [GeneratedRegex(@"^UE4SS_v(?<version>[\d.]+)-(?<build>\d+)-g(?<commit>[0-9a-fA-F]{7,40})\.zip$")]
    private static partial Regex DevAssetName();

    [GeneratedRegex(@"^UE4SS_v(?<version>\d+(\.\d+)*)\.zip$")]
    private static partial Regex OfficialAssetName();

    /// <summary>Reads "UE4SS_v3.0.1-1181-g83330635.zip" as (3.0.1, 1181, 83330635); false for any other name.</summary>
    public static bool TryParseDevAssetName(string? fileName, out string version, out int buildNumber, out string commit)
    {
        var match = DevAssetName().Match(fileName ?? "");
        version = match.Success ? match.Groups["version"].Value : "";
        commit = match.Success ? match.Groups["commit"].Value.ToLowerInvariant() : "";
        buildNumber = match.Success && int.TryParse(match.Groups["build"].Value, out var number) ? number : 0;
        return match.Success && buildNumber > 0;
    }

    /// <summary>
    /// Reads a release zip's own name: a dev build ("UE4SS_v4.0.0-6-gef15eba8.zip": 4.0.0, 6,
    /// ef15eba8) or an official release Starlink knows ("UE4SS_v4.0.0.zip": 4.0.0, no build number,
    /// the tag's commit). False for anything else, the big "zDEV-" developer zips included.
    /// </summary>
    public static bool TryParseAssetName(string? fileName, out string version, out int? buildNumber, out string commit)
    {
        if (TryParseDevAssetName(fileName, out version, out var number, out commit))
        {
            buildNumber = number;
            return true;
        }

        buildNumber = null;
        var match = OfficialAssetName().Match(fileName ?? "");
        if (match.Success && OfficialCommit(match.Groups["version"].Value) is { } official)
        {
            version = match.Groups["version"].Value;
            commit = official;
            return true;
        }

        version = "";
        commit = "";
        return false;
    }

    /// <summary>True when both commits are known and one is a prefix of the other (logs and asset names use short commits).</summary>
    public static bool SameCommit(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || Math.Min(a.Length, b.Length) < 7)
        {
            return false;
        }

        var length = Math.Min(a.Length, b.Length);
        return string.Equals(a[..length], b[..length], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for the commit of any official release (3.0.1, 4.0.0).</summary>
    public static bool IsOfficialRelease(string? commit) => OfficialVersion(commit) is not null;

    /// <summary>"4.0.0" for the 4.0.0 tag's commit; null for a dev build or an unknown commit.</summary>
    public static string? OfficialVersion(string? commit)
    {
        foreach (var release in OfficialReleases)
        {
            if (SameCommit(commit, release.Commit))
            {
                return release.Version;
            }
        }

        return null;
    }

    /// <summary>The tag commit of an official release Starlink knows ("4.0.0", "v3.0.1"); null otherwise.</summary>
    public static string? OfficialCommit(string? version)
    {
        var wanted = (version ?? "").Trim().TrimStart('v', 'V');
        foreach (var release in OfficialReleases)
        {
            if (release.Version == wanted)
            {
                return release.Commit;
            }
        }

        return null;
    }

    /// <summary>"4.0.0 (official release)", "4.0.0 dev build 6", "3.0.1 dev build 1c1a1497", or just "3.0.1" when the commit isn't known.</summary>
    public static string Describe(string? version, string? commit, int? buildNumber = null)
    {
        if (OfficialVersion(commit) is { } official)
        {
            return $"{official} (official release)";
        }

        var v = string.IsNullOrWhiteSpace(version) ? "UE4SS" : version.TrimStart('v', 'V');
        if (buildNumber is > 0)
        {
            return $"{v} dev build {buildNumber}";
        }

        return string.IsNullOrWhiteSpace(commit) ? v : $"{v} dev build {Short(commit)}";
    }

    public static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    /// <summary>The first number of a version: 4 for "4.0.0" or "v4.0.0"; null when there is none.</summary>
    public static int? Major(string? version) => ParseVersion(version)?.Major;

    /// <summary>
    /// How the candidate build relates to the installed one, without asking GitHub. The version
    /// decides first (4.0.0 is newer than every 3.0.1 build). On the same version an official
    /// release is older than any other build of it (dev builds are cut after the tag), and two known
    /// build numbers compare directly. Unknown is what is left: same version, a build number
    /// missing. Only then is the commit date worth asking GitHub for.
    /// </summary>
    public static Ue4ssBuildOrder Compare(
        string? installedVersion, string? installedCommit, int? installedBuild,
        string? candidateVersion, string? candidateCommit, int? candidateBuild)
    {
        if (SameCommit(installedCommit, candidateCommit))
        {
            return Ue4ssBuildOrder.Same;
        }

        var installed = ParseVersion(OfficialVersion(installedCommit) ?? installedVersion);
        var candidate = ParseVersion(OfficialVersion(candidateCommit) ?? candidateVersion);
        if (installed is null || candidate is null)
        {
            return Ue4ssBuildOrder.Unknown;
        }

        if (installed != candidate)
        {
            return candidate > installed ? Ue4ssBuildOrder.Newer : Ue4ssBuildOrder.Older;
        }

        if (string.IsNullOrWhiteSpace(installedCommit) || string.IsNullOrWhiteSpace(candidateCommit))
        {
            return Ue4ssBuildOrder.Unknown;
        }

        // Same version, different commits: the release itself is build 0.
        var installedNumber = IsOfficialRelease(installedCommit) ? 0 : installedBuild;
        var candidateNumber = IsOfficialRelease(candidateCommit) ? 0 : candidateBuild;
        if (installedNumber == 0 || candidateNumber == 0)
        {
            return installedNumber == 0 ? Ue4ssBuildOrder.Newer : Ue4ssBuildOrder.Older;
        }

        if (installedNumber is null || candidateNumber is null || installedNumber == candidateNumber)
        {
            return Ue4ssBuildOrder.Unknown;
        }

        return candidateNumber > installedNumber ? Ue4ssBuildOrder.Newer : Ue4ssBuildOrder.Older;
    }

    [GeneratedRegex(@"^\d+(\.\d+){0,3}")]
    private static partial Regex LeadingVersion();

    /// <summary>The leading numbers of a version as major.minor.patch, missing parts as 0; null when it starts with no number.</summary>
    private static Version? ParseVersion(string? version)
    {
        var match = LeadingVersion().Match((version ?? "").Trim().TrimStart('v', 'V'));
        if (!match.Success)
        {
            return null;
        }

        var parts = match.Value.Split('.');
        return int.TryParse(parts[0], out var major)
            && int.TryParse(parts.ElementAtOrDefault(1) ?? "0", out var minor)
            && int.TryParse(parts.ElementAtOrDefault(2) ?? "0", out var patch)
            ? new Version(major, minor, patch)
            : null;
    }
}

/// <summary>How one UE4SS build relates to another in the project's history.</summary>
public enum Ue4ssBuildOrder
{
    /// <summary>Couldn't tell: a commit is unknown, GitHub didn't answer, or the two are on different branches.</summary>
    Unknown,
    Same,
    Newer,
    Older,
}
