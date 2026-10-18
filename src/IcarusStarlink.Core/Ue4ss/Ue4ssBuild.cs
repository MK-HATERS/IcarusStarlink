using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Which UE4SS build something is. Every UE4SS build since 3.0.1 calls itself "v3.0.1" in
/// UE4SS.log, the official release and the hundreds of dev builds after it alike, so the version
/// alone can't tell them apart: the git commit can. Dev builds come from GitHub's rolling
/// "experimental-latest" release, named like "UE4SS_v3.0.1-1181-g83330635.zip"
/// (1181 commits after the v3.0.1 tag, commit 83330635).
/// </summary>
public static partial class Ue4ssBuild
{
    /// <summary>The commit of the official v3.0.1 release tag (UE4SS-RE/RE-UE4SS).</summary>
    public const string OfficialReleaseCommit = "d935b5b23bac03b65c14ae38382b02007204cc2e";

    [GeneratedRegex(@"^UE4SS_v(?<version>[\d.]+)-(?<build>\d+)-g(?<commit>[0-9a-fA-F]{7,40})\.zip$")]
    private static partial Regex DevAssetName();

    /// <summary>Reads "UE4SS_v3.0.1-1181-g83330635.zip" as (3.0.1, 1181, 83330635); false for any other name.</summary>
    public static bool TryParseDevAssetName(string? fileName, out string version, out int buildNumber, out string commit)
    {
        var match = DevAssetName().Match(fileName ?? "");
        version = match.Success ? match.Groups["version"].Value : "";
        commit = match.Success ? match.Groups["commit"].Value.ToLowerInvariant() : "";
        buildNumber = match.Success && int.TryParse(match.Groups["build"].Value, out var number) ? number : 0;
        return match.Success && buildNumber > 0;
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

    public static bool IsOfficialRelease(string? commit) => SameCommit(commit, OfficialReleaseCommit);

    /// <summary>"3.0.1 (official release)", "3.0.1 dev build 1181", "3.0.1 dev build 1c1a1497", or just "3.0.1" when the commit isn't known.</summary>
    public static string Describe(string? version, string? commit, int? buildNumber = null)
    {
        var v = string.IsNullOrWhiteSpace(version) ? "UE4SS" : version.TrimStart('v', 'V');
        if (IsOfficialRelease(commit))
        {
            return $"{v} (official release)";
        }

        if (buildNumber is > 0)
        {
            return $"{v} dev build {buildNumber}";
        }

        return string.IsNullOrWhiteSpace(commit) ? v : $"{v} dev build {Short(commit)}";
    }

    public static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;
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
