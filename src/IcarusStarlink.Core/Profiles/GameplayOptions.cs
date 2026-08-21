namespace IcarusStarlink.Core.Profiles;

/// <summary>Off/Level1/Level2 — shared by SpeedBoost, PlayerBoost, and XpBoost, whose real documented values (from classic IMM's own changelog) are each a fixed pair of levels, not a free scale.</summary>
public enum BoostLevel
{
    Off,
    Level1,
    Level2,
}

/// <summary>"Reduce crafting cost 25%/50%" per classic IMM's changelog — the only two documented levels; there's no "Level 3" to guess at.</summary>
public enum CraftCostReduction
{
    Off,
    TwentyFivePercent,
    FiftyPercent,
}

/// <summary>
/// "Merge options" per the spec — gameplay-wide toggles applied as a final pass after the merge
/// queue, not as another queued mod (matches classic IMM's own documented behavior: "these new
/// options are added after the mods are all merged. By doing this it effects the custom item mods
/// also"). SpeedBoost/PlayerBoost/XpBoost/CraftCost have real documented before-after values from
/// classic IMM's changelog; Stacks/Slots/SpeedCrafting don't (classic IMM never published an exact
/// multiplier for those), so those three are a user-supplied multiplier/percentage instead of a
/// fixed level — see IcarusStarlink.PakIO.GameplayOptions.GameplayOptionsApplier for exactly which
/// real fields each option writes.
/// </summary>
public sealed class GameplayOptions
{
    public BoostLevel SpeedBoost { get; set; }

    public BoostLevel PlayerBoost { get; set; }

    public BoostLevel XpBoost { get; set; }

    public CraftCostReduction CraftCost { get; set; }

    /// <summary>Multiplies every item's MaxStack (e.g. 3 = triple stack sizes). Null or &lt;= 0 = unchanged.</summary>
    public double? StacksMultiplier { get; set; }

    /// <summary>Multiplies every inventory/container's StartingSlots. Null or &lt;= 0 = unchanged.</summary>
    public double? SlotsMultiplier { get; set; }

    /// <summary>Percent (0-100) to reduce RequiredMillijoules by, for recipes with no Water/Milk/Biofuel resource input or output — matches classic IMM's own documented conditional logic. Null or &lt;= 0 = unchanged.</summary>
    public double? SpeedCraftingReductionPercent { get; set; }

    public bool RemoveWeight { get; set; }

    public bool UnlimitedAmmo { get; set; }

    public bool DisableTemperatures { get; set; }
}
