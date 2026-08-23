namespace IcarusStarlink.Diffing;

/// <summary>
/// Shared "is this new-item change actually a stale edit of a row the game removed/renamed"
/// signal — field count is the only clue available: a real new item defines many fields, while a
/// mod editing an item this game version no longer has usually defines just one or two. Shared
/// between TableApplier's post-merge report (Rebuild time) and Library's proactive per-mod check
/// (no Rebuild needed) so the two can't drift apart on wording or cutoff.
/// </summary>
public static class StaleItemHeuristic
{
    public const int LikelyStaleMaxFieldCount = 2;

    public static bool IsLikelyStale(int fieldCount) => fieldCount <= LikelyStaleMaxFieldCount;

    /// <summary>
    /// Deliberately not phrased as an error — adding new content is exactly what many mods
    /// legitimately do. The field count is the useful signal: a real new item defines many
    /// fields, while one or two usually means a mod editing a row this game version no longer has.
    /// </summary>
    public static string BuildNote(string currentFile, string itemName, int fieldCount) =>
        $"'{itemName}' isn't in the game's current {currentFile} — created as a new item ({fieldCount} field(s)). "
        + "That's normal for a mod that adds content; if this mod only meant to edit existing items, it's likely out of date for this game version and needs editing.";
}
