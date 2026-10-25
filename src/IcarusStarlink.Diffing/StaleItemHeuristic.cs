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
    /// <param name="modName">The mod the row came from, when known.</param>
    public static string BuildNote(string currentFile, string itemName, int fieldCount, string? modName = null) =>
        $"{(modName is null ? "" : $"{modName}: ")}'{itemName}' isn't in the game's {currentFile}, so it was added as a new row with only "
        + $"{(fieldCount == 1 ? "1 field" : $"{fieldCount} fields")}. If the mod meant to change the game's row, the game has renamed or removed it and this change does nothing.";
}
