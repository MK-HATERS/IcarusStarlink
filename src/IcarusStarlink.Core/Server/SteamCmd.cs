using System.Globalization;
using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Server;

/// <summary>
/// SteamCMD command lines and output parsing for the Icarus dedicated server (app 2089300,
/// anonymous login). Pure text in, values out, so all of it is testable without running SteamCMD.
/// </summary>
public static partial class SteamCmd
{
    /// <summary>Valve's official SteamCMD for Windows download (developer.valvesoftware.com/wiki/SteamCMD).</summary>
    public const string WindowsDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    public static readonly IReadOnlyList<string> Branches = ["public", "experimental"];

    /// <summary>
    /// Install or update into <paramref name="installFolder"/> on <paramref name="branch"/>.
    /// force_install_dir comes before login (newer SteamCMD warns otherwise), and -beta is always
    /// passed: the Icarus wiki notes that rolling back from experimental REQUIRES -beta public — just
    /// leaving the flag off keeps the experimental build.
    /// </summary>
    public static string BuildInstallArguments(string installFolder, string branch, bool validate = true) =>
        $"+force_install_dir \"{installFolder.TrimEnd('\\', '/')}\" +login anonymous +app_update {DedicatedServerDefaults.SteamAppId} -beta {NormalizeBranch(branch)}{(validate ? " validate" : "")} +quit";

    /// <summary>Prints the app's current Steam info (branches and their build ids) without downloading anything.</summary>
    public static string BuildAppInfoArguments() =>
        $"+login anonymous +app_info_update 1 +app_info_print {DedicatedServerDefaults.SteamAppId} +quit";

    public static string NormalizeBranch(string? branch) =>
        string.Equals(branch?.Trim(), "experimental", StringComparison.OrdinalIgnoreCase) ? "experimental" : "public";

    /// <summary>"Update state (0x61) downloading, progress: 39.84 (3912345600 / 9820000000)" → 39.84; null for any other line.</summary>
    public static double? ParseProgressPercent(string line)
    {
        var match = ProgressPattern().Match(line);
        return match.Success && double.TryParse(match.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) ? pct : null;
    }

    /// <summary>The phase word of a progress line ("downloading", "verifying install", "preallocating", "committing"), or null.</summary>
    public static string? ParseProgressPhase(string line)
    {
        var match = ProgressPattern().Match(line);
        return match.Success ? match.Groups["phase"].Value.Trim() : null;
    }

    public static bool IsSuccessLine(string line) =>
        line.Contains($"Success! App '{DedicatedServerDefaults.SteamAppId}'", StringComparison.OrdinalIgnoreCase);

    /// <summary>SteamCMD reports failures as "ERROR! …" or "Error! …" lines (no subscription, disk full as state 0x202, timeouts).</summary>
    public static bool IsErrorLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("ERROR!", StringComparison.Ordinal) || trimmed.StartsWith("Error!", StringComparison.Ordinal)
            || trimmed.Contains("Failed to install app", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The installed build and branch from steamapps\appmanifest_2089300.acf (written by SteamCMD
    /// next to a force_install_dir install): "buildid" and UserConfig's "BetaKey" (absent = public).
    /// </summary>
    public static (int? BuildId, string Branch) ReadAppManifest(string acfText)
    {
        int? buildId = BuildIdPattern().Match(acfText) is { Success: true } m && int.TryParse(m.Groups["id"].Value, out var id) ? id : null;
        var beta = BetaKeyPattern().Match(acfText);
        return (buildId, beta.Success ? NormalizeBranch(beta.Groups["key"].Value) : "public");
    }

    /// <summary>The build id of <paramref name="branch"/> from app_info_print output (the "branches" block), or null when it isn't there.</summary>
    public static int? ParseLatestBuildId(string appInfoOutput, string branch)
    {
        var branchesAt = appInfoOutput.IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
        if (branchesAt < 0)
        {
            return null;
        }

        var name = NormalizeBranch(branch);
        var branchAt = appInfoOutput.IndexOf($"\"{name}\"", branchesAt, StringComparison.OrdinalIgnoreCase);
        if (branchAt < 0)
        {
            return null;
        }

        var match = BuildIdPattern().Match(appInfoOutput, branchAt);
        return match.Success && int.TryParse(match.Groups["id"].Value, out var id) ? id : null;
    }

    /// <summary>The steamcmd.exe that installed this server, from the manifest's "LauncherPath" (VDF-escaped backslashes undone); null when absent.</summary>
    public static string? ReadLauncherPath(string acfText)
    {
        var match = LauncherPathPattern().Match(acfText);
        return match.Success && match.Groups["p"].Value.Length > 0 ? match.Groups["p"].Value.Replace(@"\\", @"\") : null;
    }

    [GeneratedRegex("\"LauncherPath\"\\s+\"(?<p>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LauncherPathPattern();

    [GeneratedRegex(@"Update state \(0x[0-9a-fA-F]+\)\s*(?<phase>[^,]*),\s*progress:\s*(?<pct>[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressPattern();

    [GeneratedRegex("\"buildid\"\\s+\"(?<id>[0-9]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex BuildIdPattern();

    [GeneratedRegex("\"BetaKey\"\\s+\"(?<key>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex BetaKeyPattern();
}
