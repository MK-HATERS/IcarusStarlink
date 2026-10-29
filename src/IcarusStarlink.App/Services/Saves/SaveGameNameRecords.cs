namespace IcarusStarlink.App.Services;

/// <param name="MaxRank">The most ranks this talent can hold, from its own Rewards tiers — what the editor caps rank controls at.</param>
/// <param name="IsDefaultUnlocked">The row's own bDefaultUnlocked — the game grants it from the start WITHOUT writing it into the save, so the editor must show it as unlocked even at rank 0.</param>
/// <param name="Cost">Workshop research only: "50 Ren" (D_WorkshopItems ResearchCost).</param>
/// <param name="Order">Its row's place in D_Talents: missions are listed in the game's own order (a map's first area first).</param>
public sealed record TalentInfo(string DisplayName, string Description, string Tree, int MaxRank, bool IsDefaultUnlocked, string? Cost = null, int Order = int.MaxValue);

/// <param name="Display">The tree's own name ("Resources"); for a blueprint tier, the bench it's for ("Crafting Bench").</param>
/// <param name="ArchetypeDisplay">Its archetype's ("Survival", "Tier 1", "Olympus", "Envirosuits").</param>
/// <param name="Model">Player, Blueprint, Solo (a character's), Workshop or Prospect (the account's).</param>
/// <param name="ArchetypeRow">Its D_TalentArchetypes row ("Creature_Raptor"): a mount's talents are the trees of its archetype.</param>
/// <param name="Order">Where its group is listed: the game's own tree order, blueprint tiers 1 to 5; int.MaxValue for a tree the tables don't place.</param>
public sealed record TreeInfo(string Display, string ArchetypeDisplay, string Model, string ArchetypeRow = "", int Order = int.MaxValue)
{
    /// <summary>
    /// "Survival · Resources" for a character's trees; just "Armors" / "Elysium" for workshop and
    /// mission trees, whose own names only repeat the archetype's ("Workshop Armors", "Prospect Elysium").
    /// </summary>
    public string Group => Model is "Workshop" or "Prospect" || string.Equals(Display, ArchetypeDisplay, StringComparison.OrdinalIgnoreCase)
        ? ArchetypeDisplay
        : $"{ArchetypeDisplay} · {Display}";
}

public sealed record AccoladeInfo(string DisplayName, string Description, string Category, int GoalCount);

/// <param name="RequiredFlags">(table, row): D_AccountFlags, D_CharacterFlags or D_DLCPackageData.</param>
/// <param name="GrantedFlagsByRank">The character flags each rank gives (Rewards[i].GrantedFlags).</param>
/// <param name="IsFreeToReach">A Reroute node or unlocked from the start: counts as met when it's another talent's prerequisite.</param>
public sealed record TalentRule(
    IReadOnlyList<(string Table, string Row)> RequiredFlags, IReadOnlyList<string> RequiredTalents,
    IReadOnlyList<IReadOnlyList<string>> GrantedFlagsByRank, bool IsFreeToReach);

/// <summary>What a flag is for, on the Saves page's Unlocks tab.</summary>
public enum FlagKind
{
    /// <summary>A mission's reward: unlocks blueprints or workshop items. Worth editing.</summary>
    Reward,

    /// <summary>Gives bonus talent points (D_PlayerTalentModifiers). Worth editing.</summary>
    TalentPoints,

    /// <summary>A one-off marker or story step the game sets itself.</summary>
    Marker,

    /// <summary>A "has seen" pop-up.</summary>
    Prompt,

    /// <summary>Set by a learned talent: shown read-only.</summary>
    FromTalent,

    /// <summary>Deprecated, tooltip-only, test, or granted by nothing: hidden unless asked for.</summary>
    Unused,

    /// <summary>Not in the game's table at all.</summary>
    Unknown,
}

/// <param name="Detail">What it does, where it comes from — the row's second line.</param>
public sealed record FlagInfo(string RowName, string Display, string Detail, FlagKind Kind);

/// <param name="PointsRequired">The row's own TotalPointsRequired — what the editor treats as "maxed out" for a Set to max action.</param>
/// <param name="ImagePath">The row's own raw "Image" field (a "/Game/…/T_Bestiary_Xxx.T_Bestiary_Xxx" texture reference) — null for a row that never carried one, or when the table itself is missing.</param>
/// <param name="Group">Where it lives, from its first D_BestiaryData Biomes row: "Forest", "Arctic", "Great Hunt arenas".</param>
/// <param name="GroupOrder">Where its group is listed: biomes in the order D_BestiaryData first lists them, "Other" last.</param>
public sealed record BestiaryCreatureInfo(string DisplayName, int PointsRequired, bool IsBoss, string? ImagePath, string Group = "", int GroupOrder = int.MaxValue);

/// <param name="IconPath">The Itemable row's own raw "Icon" field (a "/Game/…/ITEM_Xxx.ITEM_Xxx" texture reference) — null for a row that never carried one, or when the table itself is missing.</param>
public sealed record ItemInfo(string DisplayName, int Weight, int MaxStack, string? IconPath);
