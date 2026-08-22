using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// The one place that knows the nexusmods.com/icarus/mods/&lt;id&gt; web-page URL shape — parsing it
/// (Downloads' Add mod URL, Library's Link to Nexus ID dialog) and building it (Library's Open on
/// Nexus). Previously each consumer carried its own copy of the same regex, which is exactly the
/// drift risk CredentialTargets already exists to prevent for its own shared string. Distinct from
/// NxmUrl, which parses the nxm:// protocol scheme, not the website's own page URLs.
/// </summary>
public static partial class NexusModWebUrl
{
    [GeneratedRegex(@"nexusmods\.com/icarus/mods/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModPagePattern();

    /// <summary>Extracts the mod ID from a full mod-page URL, or accepts a bare positive integer directly (the Link to Nexus dialog takes either). False for anything else.</summary>
    public static bool TryParseModId(string text, out int modId)
    {
        var trimmed = text.Trim();
        var match = ModPagePattern().Match(trimmed);
        if (match.Success)
        {
            modId = int.Parse(match.Groups[1].Value);
            return true;
        }

        return int.TryParse(trimmed, out modId) && modId > 0;
    }

    /// <summary>True only for a real mod-page URL — Downloads' watchlist stores the URL itself, so a bare numeric ID isn't enough there.</summary>
    public static bool TryParseModIdFromUrl(string url, out int modId)
    {
        var match = ModPagePattern().Match(url);
        if (match.Success)
        {
            modId = int.Parse(match.Groups[1].Value);
            return true;
        }

        modId = 0;
        return false;
    }

    public static string For(int modId) => $"https://www.nexusmods.com/icarus/mods/{modId}";
}
