namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Which UE4SS build sits in a ue4ss folder. UE4SS names its build in UE4SS.log, but only rewrites
/// that log when the game next starts, so right after Starlink installs a build the log still names
/// the old one. Starlink therefore deletes the stale log when it installs and leaves a one-line
/// marker (version and commit) that is read until UE4SS writes a fresh log.
/// </summary>
public static class Ue4ssInstalledBuild
{
    public const string MarkerFileName = "UE4SS.starlink-build.txt";
    public const string LocalSourceMarkerFileName = "UE4SS.starlink-local.txt";

    /// <summary>Version and commit from UE4SS.log, else from Starlink's marker; nulls when neither says.</summary>
    public static (string? Version, string? Commit) Read(string ue4ssFolder)
    {
        var lines = ReadFirstLines(Path.Combine(ue4ssFolder, "UE4SS.log"), 20);
        if (Ue4ssLogVersionParser.Parse(lines) is { } version)
        {
            return (version, Ue4ssLogVersionParser.ParseCommit(lines));
        }

        var marker = ReadFirstLines(Path.Combine(ue4ssFolder, MarkerFileName), 2);
        return marker.Count == 2 && marker[0].Length > 0
            ? (marker[0], marker[1].Length > 0 ? marker[1].ToLowerInvariant() : null)
            : (null, null);
    }

    /// <summary>After installing a build: drops the stale UE4SS.log and records what was installed (or removes the marker when the build isn't known).</summary>
    public static void RecordInstalled(string ue4ssFolder, string? version, string? commit)
    {
        TryDelete(Path.Combine(ue4ssFolder, "UE4SS.log"));
        var markerPath = Path.Combine(ue4ssFolder, MarkerFileName);
        if (string.IsNullOrWhiteSpace(version))
        {
            TryDelete(markerPath);
            return;
        }

        File.WriteAllLines(markerPath, [version.TrimStart('v', 'V'), commit ?? ""]);
    }

    private static List<string> ReadFirstLines(string path, int count)
    {
        var lines = new List<string>(count);
        if (!File.Exists(path))
        {
            return lines;
        }

        try
        {
            // UE4SS keeps its log open while the game runs: read it shared.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (lines.Count < count && reader.ReadLine() is { } line)
            {
                lines.Add(line.Trim());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable right now: the build just shows as unknown.
        }

        return lines;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log the running game holds open: harmless, it gets rewritten on the next launch.
        }
    }
}
