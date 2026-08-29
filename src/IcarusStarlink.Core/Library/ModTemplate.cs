namespace IcarusStarlink.Core.Library;

/// <summary>
/// Real, official starting-point skeletons for "New mod…" — Blank keeps today's existing behavior
/// (an empty EXMOD with no fields yet); the other two are sourced directly from classic IMM's own
/// published Blank_Craftable_Item.EXMODZ/Blank_Consumable_Item.EXMODZ templates (Jimk72's own
/// GitHub), not invented — each is a genuinely complete, working item definition reusing real
/// vanilla assets as placeholders, with the placeholder name substituted for whatever the user
/// actually typed.
/// </summary>
public enum ModTemplate
{
    Blank,
    CraftableOrDeployableItem,
    ConsumableItem,
}
