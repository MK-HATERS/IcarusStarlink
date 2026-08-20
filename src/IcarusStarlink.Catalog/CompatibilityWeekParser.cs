namespace IcarusStarlink.Catalog;

/// <summary>
/// Real catalog data (verified against the live Daedalus mods.json during Phase 4 planning) is
/// inconsistent: "w139", "W184" (different case), a bare "215" with no prefix, and Jimk72's
/// catalog always uses the literal string "All". This tries to pull a week number out of any of
/// those; anything else (including "All") is deliberately not an error — it just means "no
/// specific week compatibility claimed", not "malformed data".
/// </summary>
public static class CompatibilityWeekParser
{
    public static int? Parse(string? compatibility)
    {
        if (string.IsNullOrWhiteSpace(compatibility))
        {
            return null;
        }

        var trimmed = compatibility.Trim();
        var digits = trimmed.Length > 0 && (trimmed[0] == 'w' || trimmed[0] == 'W')
            ? trimmed[1..]
            : trimmed;

        return int.TryParse(digits, out var week) ? week : null;
    }
}
