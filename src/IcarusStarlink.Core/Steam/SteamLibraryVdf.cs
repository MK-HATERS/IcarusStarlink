using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Steam;

/// <summary>
/// Parses just enough of Valve's own KeyValues ("VDF") text format to pull every library root out
/// of a real libraryfolders.vdf — not a general VDF parser, since the input shape here is entirely
/// Steam-generated and never hand-authored. Confirmed against the user's own real file during
/// Phase 7.5 planning: each library is a numbered top-level block containing a "path" key, with
/// backslashes escaped as "\\" the way Steam itself writes them.
/// </summary>
public static class SteamLibraryVdf
{
    private static readonly Regex PathKeyPattern = new("\"path\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    public static IReadOnlyList<string> ParseLibraryPaths(string vdfContent)
    {
        var paths = new List<string>();
        foreach (Match match in PathKeyPattern.Matches(vdfContent))
        {
            paths.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }

        return paths;
    }
}
