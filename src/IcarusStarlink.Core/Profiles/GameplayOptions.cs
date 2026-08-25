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
/// "Merge options" per the spec — gameplay-wide toggles that layer on top of whatever the queue
/// merges. Two structurally different groups, per IcarusStarlink.PakIO.GameplayToggles' own split:
/// SpeedBoost/PlayerBoost/XpBoost/DisableTemperatures/RemoveLevelCap each write a single fixed row,
/// so they're real MergeEngine participants — a queued mod touching the same field shows as a real,
/// resolvable conflict instead of being silently overwritten (GameplayOptionsFieldChangeGenerator).
/// Everything else (Stacks/Slots/CraftCost/SpeedCrafting/RemoveWeight/UnlimitedAmmo/TamingSpeed)
/// broadcasts a scale/set transform across every row of a whole table, deliberately still applied as
/// a genuine final pass AFTER the merge queue resolves — matching classic IMM's own documented
/// behavior ("these new options are added after the mods are all merged. By doing this it effects
/// the custom item mods also"), which a field-level conflict model can't express for a compounding
/// transform (GameplayOptionsApplier). SpeedBoost/PlayerBoost/XpBoost/CraftCost have real documented
/// before-after values from classic IMM's changelog; Stacks/Slots/SpeedCrafting/TamingSpeed don't
/// (classic IMM never published an exact multiplier for those), so those are a user-supplied
/// multiplier/percentage instead of a fixed level — see GameplayOptionsFieldChangeGenerator/
/// GameplayOptionsApplier for exactly which real fields each option writes.
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

    /// <summary>
    /// True if any Category-1 ("single fixed row," a real MergeEngine participant via
    /// GameplayOptionsFieldChangeGenerator) option is on. Mirrors that class's own per-row gating —
    /// RemoveLevelCap targets a different real file than the other four, so the generator still
    /// gates each row's own read separately — but this is the one place that answers "is ANY
    /// Category-1 option active at all," for a caller that only cares whether there's real work to
    /// do, not which specific file is involved.
    /// </summary>
    public bool HasCategory1Active =>
        SpeedBoost != BoostLevel.Off || PlayerBoost != BoostLevel.Off || XpBoost != XpBoostLevel.Off
        || DisableTemperatures || RemoveLevelCap;

    /// <summary>Same idea for Category-2 ("broadcast to every row," GameplayOptionsApplier's own compounding final pass) — matches GameplayOptionsApplier.RequiredCurrentFiles' own condition list exactly.</summary>
    public bool HasCategory2Active =>
        StacksMultiplier is > 0 || SlotsMultiplier is > 0 || RemoveWeight || CraftCost != CraftCostReduction.Off
        || SpeedCraftingReductionPercent is > 0 || UnlimitedAmmo || TamingSpeedReductionPercent is > 0;

    /// <summary>
    /// Whether Rebuild has ANY gameplay-option work to do at all. The single source of truth for
    /// that question — previously it was independently re-derived in at least two places (the
    /// Merge &amp; Install ViewModel's own active-options summary, and implicitly by whichever
    /// PakIO classification a caller happened to check), which is exactly the duplication class
    /// that once let one of them go stale: a rebuild guard that checked only a subset of options let
    /// Rebuild silently no-op while Install still shipped a stale pak, with no error. A future option
    /// only needs adding to HasCategory1Active/HasCategory2Active above, not re-derived per caller.
    /// </summary>
    public bool IsAnyActive => HasCategory1Active || HasCategory2Active;
}
