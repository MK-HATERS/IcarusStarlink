using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// UE4SS.log's first line is always of the form "UE4SS - v3.0.1 Beta #0 - Git SHA #..." — the only
/// real version marker available for a real installed UE4SS.dll, confirmed against the user's own
/// real, working install (UE4SS.dll itself carries no Win32 file-version resource at all). Pure and
/// line-oriented rather than tied to reading a real file, so it's directly testable against a
/// captured fixture.
/// </summary>
public static partial class Ue4ssLogVersionParser
{
    [GeneratedRegex(@"UE4SS\s*-\s*v(\S+)")]
    private static partial Regex VersionPattern();

    /// <summary>Scans the first few lines (the version line isn't guaranteed to be the very first — a blank/console-setup line can precede it) for the version marker. Returns null if the log is empty, unreadable, or doesn't match the expected shape.</summary>
    public static string? Parse(IEnumerable<string> logLines)
    {
        foreach (var line in logLines.Take(10))
        {
            var match = VersionPattern().Match(line);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }
}
