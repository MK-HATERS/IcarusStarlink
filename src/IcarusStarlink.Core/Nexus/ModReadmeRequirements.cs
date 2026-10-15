using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// Nexus's own API doesn't expose a mod's requirements, but mod authors routinely ship a README
/// with a REQUIREMENTS section (confirmed against real downloads: "REQUIREMENTS / - UE4SS … / -
/// ArcFission Library 1.2.0 or newer (separate download)"). Surfacing those lines at activation,
/// with a best-effort "already installed?" check, is what lets a user see WHICH other download a
/// mod needs instead of discovering it in-game.
/// </summary>
public static partial class ModReadmeRequirements
{
    [GeneratedRegex(@"^\s*#*\s*requirements?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^[A-Z][A-Z0-9 ()/&'-]{2,}$")]
    private static partial Regex AllCapsHeaderPattern();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();

    /// <summary>The bullet lines under a "REQUIREMENTS" (or "# Requirements") heading, up to the next blank line or heading; empty when there's no such section.</summary>
    public static IReadOnlyList<string> Parse(string readmeText)
    {
        var lines = readmeText.Replace("\r\n", "\n").Split('\n');
        var requirements = new List<string>();
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!inSection)
            {
                inSection = HeaderPattern().IsMatch(line);
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#') || AllCapsHeaderPattern().IsMatch(line))
            {
                if (requirements.Count > 0)
                {
                    break;
                }

                continue;
            }

            requirements.Add(line.TrimStart('-', '*', '•', ' ').Trim());
        }

        return requirements;
    }

    /// <summary>
    /// The part of a requirement line that names the thing: everything before a version number,
    /// "or newer", a parenthesis, a colon or a URL — "ArcFission Library 1.2.0 or newer (separate
    /// download)" → "ArcFission Library".
    /// </summary>
    public static string ExtractName(string requirement)
    {
        var cut = requirement;
        foreach (var marker in new[] { "(", ":", "http", " v", " or ", " - " })
        {
            var at = cut.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                cut = cut[..at];
            }
        }

        var words = cut.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(w => !char.IsDigit(w[0]))
            .ToList();
        return words.Count > 0 ? string.Join(' ', words) : requirement;
    }

    /// <summary>Whether something installed matches the requirement's name, ignoring case, spaces and punctuation ("ArcFission Library" ↔ staged folder "ArcFissionLibrary").</summary>
    public static bool IsSatisfiedBy(string requirement, IEnumerable<string> installedNames)
    {
        var wanted = Normalize(ExtractName(requirement));
        if (wanted.Length < 3)
        {
            return false;
        }

        return installedNames.Select(Normalize).Any(have => have.Contains(wanted) || (wanted.Contains(have) && have.Length >= 4));
    }

    /// <summary>True for the loader itself ("UE4SS (experimental build)…") — not a mod, so not something the Library can tick off; shown but never flagged as missing.</summary>
    public static bool IsLoaderRequirement(string requirement) => Normalize(ExtractName(requirement)).Contains("ue4ss");

    private static string Normalize(string value) => NonAlphanumeric().Replace(value.ToLowerInvariant(), "");
}
