namespace IcarusStarlink.Core.Library;

/// <summary>
/// Real, official starting-point skeletons for "New mod…" — Blank keeps today's existing behavior
/// (an empty EXMOD with no fields yet); CraftableOrDeployableItem/ConsumableItem are sourced from
/// classic IMM's own published Blank_Craftable_Item.EXMODZ/Blank_Consumable_Item.EXMODZ templates
/// (Jimk72's own GitHub); BuildingPiece/ElectricGenerator/WaterPump are the three other real
/// entries from that same tool's own bundled NewItemOptions.json ("Add Item to Mod... premade
/// templates" — the same file classic IMM's own real editor offers). None of these are invented —
/// each is a genuinely complete, working item definition reusing real vanilla assets (or, for the
/// three newer ones, a real placeholder Blueprint path an author is expected to replace with their
/// own art) as a starting point, with the placeholder name substituted for whatever the user
/// actually typed.
/// </summary>
public enum ModTemplate
{
    Blank,
    CraftableOrDeployableItem,
    ConsumableItem,
    BuildingPiece,
    ElectricGenerator,
    WaterPump,

    /// <summary>
    /// Not an item: a set of "scale a field across a table" rules (ScaleRules) for the common
    /// quality-of-life changes — stack sizes, weight, crafting/taming/growth time, spoil and buff
    /// time, storage — each with its own on/off and factor, modeled on what real QoL mods change
    /// (Deyvid's All-in-One, Nexus 317). The rows are built from the user's own game data.
    /// </summary>
    QualityOfLife,

    // New items made from a game item with the editor's new-item wizard (ItemCloner), which opens
    // straight away preset for the kind: what to copy, and how the item is obtained.

    /// <summary>A new envirosuit (own name, icon and armour stats), bought in the orbital workshop's Envirosuits tab.</summary>
    Envirosuit,

    /// <summary>A new suit module (own name, icon and stat bonuses), bought in the workshop's Modules tab.</summary>
    EnvirosuitModule,

    /// <summary>A new food/medicine with its own buff (D_ModifierStates row), cooked or crafted at a bench.</summary>
    ConsumableWithBuff,

    /// <summary>Any game item as a new orbital-workshop purchase, in a workshop tab of the mod's own.</summary>
    WorkshopItem,

    /// <summary>A D_MapIcons row (a map/compass marker) — what a blueprint or Lua mod shows by name.</summary>
    MapIcon,
}
