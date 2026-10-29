namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The order the Saves page's grouped lists show their groups and rows in. A list view shows its
/// groups in the order their first row comes, so sorting the rows sorts the groups: talent trees
/// in the game's own order with blueprints Tier 1 to Tier 5 (TreeInfo.Order, from the game's
/// tables), biomes as the bestiary table lists them. A group the tables can't place (a mod's
/// tree, a row the game lacks) comes after, by name.
/// </summary>
public static class SaveGroupOrder
{
    /// <summary>Talents and workshop research: groups in order, rows by name.</summary>
    public static IEnumerable<SaveTalentViewModel> Talents(IEnumerable<SaveTalentViewModel> rows) =>
        rows.OrderBy(t => t.GroupOrder)
            .ThenBy(t => t.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Missions: maps in order, each map's missions in the game's own order (its first area first).</summary>
    public static IEnumerable<SaveTalentViewModel> Missions(IEnumerable<SaveTalentViewModel> rows) =>
        rows.OrderBy(t => t.GroupOrder)
            .ThenBy(t => t.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Order)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Bestiary: biomes in order, creatures in the table's order.</summary>
    public static IEnumerable<SaveBestiaryEntryViewModel> Bestiary(IEnumerable<SaveBestiaryEntryViewModel> rows) =>
        rows.OrderBy(b => b.GroupOrder)
            .ThenBy(b => b.Group, StringComparer.OrdinalIgnoreCase);
}
