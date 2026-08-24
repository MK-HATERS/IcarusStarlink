namespace IcarusStarlink.Core.Profiles;

/// <summary>Off/Level1/Level2 — shared by SpeedBoost and PlayerBoost, whose real documented values (from classic IMM's own changelog) are each a fixed pair of levels, not a free scale.</summary>
public enum BoostLevel
{
    Off,
    Level1,
    Level2,
}

/// <summary>Off/Level1/Level2/Level3 — XpBoost's own levels, kept separate from BoostLevel because classic IMM's live changelog grew XpBoost a third tier (200%/500%/1000%) that SpeedBoost/PlayerBoost never got; a shared enum would let a user pick a Level3 those two have no documented values for.</summary>
public enum XpBoostLevel
{
    Off,
    Level1,
    Level2,
    Level3,
}

/// <summary>"Reduce crafting cost 25%/50%", plus "Creative mode... reduce all crafting bench costs to 0" — all three per classic IMM's live changelog (Creative added after the original two-tier research this enum was first built from).</summary>
public enum CraftCostReduction
{
    Off,
    TwentyFivePercent,
    FiftyPercent,
    Creative,
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

    public XpBoostLevel XpBoost { get; set; }

    public CraftCostReduction CraftCost { get; set; }

    /// <summary>Multiplies every item's MaxStack (e.g. 3 = triple stack sizes). Null or &lt;= 0 = unchanged.</summary>
    public double? StacksMultiplier { get; set; }

    /// <summary>Multiplies every inventory/container's StartingSlots. Null or &lt;= 0 = unchanged.</summary>
    public double? SlotsMultiplier { get; set; }

    /// <summary>Percent (0-100) to reduce RequiredMillijoules by, for recipes with no Water/Milk/Biofuel resource input or output — matches classic IMM's own documented conditional logic. Null or &lt;= 0 = unchanged.</summary>
    public double? SpeedCraftingReductionPercent { get; set; }

    /// <summary>Percent (0-100) to reduce every creature's TameDurationInSeconds by (AI-D_Tames.json). No documented value from classic IMM (this option doesn't exist there), so it's a free user-supplied percentage, matching SpeedCraftingReductionPercent's own precedent. Null or &lt;= 0 = unchanged.</summary>
    public double? TamingSpeedReductionPercent { get; set; }

    public bool RemoveWeight { get; set; }

    public bool UnlimitedAmmo { get; set; }

    public bool DisableTemperatures { get; set; }

    /// <summary>Sets Character-D_CharacterGrowth.json's "Player" row MaxDisplayLevel/MaxLevel to 50000 (real base values: 60/1000) — a plain on/off, not a free number, matching a real community mod's own chosen value (classic IMM never documented one, this option doesn't exist there).</summary>
    public bool RemoveLevelCap { get; set; }
}
