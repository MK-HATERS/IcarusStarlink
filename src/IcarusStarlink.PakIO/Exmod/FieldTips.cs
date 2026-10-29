namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// A plain name and a one-line tip for the game fields mods change most (counted over 61 real
/// mods, 2026-10-28: recipes, items, deployables, buildings, buffs, workshop, talents,
/// inventories, farming, heat). Shown under each field in the mod editor and on the Editor page
/// when GameItemEditor's own short list doesn't have the field. Only what the field is known to
/// do; a field nobody is sure about gets no tip rather than a guess.
/// </summary>
public static class FieldTips
{
    public static bool TryGet(string field, out (string Label, string Tip) tip) => Tips.TryGetValue(field, out tip);

    private static readonly Dictionary<string, (string Label, string Tip)> Tips = new(StringComparer.OrdinalIgnoreCase)
    {
        // Recipes (D_ProcessorRecipes)
        ["RecipeSets"] = ("Made at", "Which benches or tools can make it. Add one to make it at another bench."),
        ["Inputs"] = ("Ingredients", "The items it costs, each with how many. Lower counts = cheaper."),
        ["Outputs"] = ("Makes", "What you get and how many. Higher count = more per craft."),
        ["ResourceInputs"] = ("Uses while crafting", "Water, power, fuel or other resources the bench uses for it."),
        ["QueryInputs"] = ("Any-of-a-kind ingredients", "Costs like \"any raw meat\": any item of that kind counts."),
        ["Requirement"] = ("Needs blueprint", "The blueprint (talent) you must learn before the recipe shows. Empty = known from the start."),
        ["ResourceCostMultipliers"] = ("Resource cost changes", "Multiplies how much water, power or fuel it uses. 1 = normal, lower = less."),
        ["ExperienceMultiplier"] = ("XP multiplier", "Higher = more XP from it, lower = less; 1 = normal."),

        // Items (D_ItemsStatic and the rows it links)
        ["Manual_Tags"] = ("Tags", "Tags the game sorts items by: what fits a slot, what counts as fuel, food, a tool. Changing them changes where the item fits."),
        ["Generated_Tags"] = ("Tags (made by the game)", "Tags the game works out from the item's other rows. Usually left as they are."),
        ["Itemable"] = ("Name, weight and stack row", "Links to the row with its name, icon, weight and stack size."),
        ["Decayable"] = ("Decay row", "Links to how it spoils or decays. Empty = it never decays."),
        ["Durable"] = ("Durability row", "Links to how much wear it takes and what repairing it costs. Empty = it never wears out."),
        ["Usable"] = ("Use row", "Links to what happens when you use it."),
        ["Consumable"] = ("Eat or drink row", "Links to what eating, drinking or using it up does."),
        ["Deployable"] = ("Placeable row", "Links to how it's placed in the world."),
        ["Buildable"] = ("Building piece row", "Links to its building piece settings."),
        ["Armour"] = ("Armour row", "Links to its armour stats and look."),
        ["Inventory"] = ("Storage row", "Links to its own inventory: how many slots it has."),
        ["Meshable"] = ("Model row", "Links to the 3D model it uses."),
        ["Focusable"] = ("Look-at row", "Links to what shows when you look at it."),
        ["Highlightable"] = ("Highlight row", "Links to how it's outlined when you look at it."),
        ["Interactable"] = ("Interact row", "Links to what pressing the interact key on it does."),
        ["Actionable"] = ("Actions row", "Links to the actions it can do."),
        ["Transmutable"] = ("Fuel / conversion row", "Links to what it can be burned or turned into."),
        ["Processing"] = ("Crafting station row", "Links to its crafting settings when it's a bench or machine."),
        ["Floatable"] = ("Floats row", "Links to how it floats in water."),
        ["Flammable"] = ("Burns row", "Links to how it catches fire."),
        ["Hitable"] = ("Hit row", "Links to what happens when it's hit."),
        ["Resource"] = ("Resource row", "Links to the resource (water, power, fuel) it holds or makes."),
        ["Experience"] = ("XP row", "Links to the XP you get from it."),
        ["CraftingExperience"] = ("Crafting XP", "XP for crafting it. Higher = more XP."),
        ["AdditionalStats"] = ("Extra stats", "Stats it gives on top of its other rows."),
        ["Icon"] = ("Icon", "The picture shown in the inventory and menus."),
        ["ItemStaticData"] = ("Item row", "Which item (D_ItemsStatic row) this is."),
        ["ItemDynamicData"] = ("Starting values", "Values a new copy starts with, like durability or filled amount."),
        ["ItemCustomStats"] = ("Item's own stats", "Stats this copy of the item carries."),

        // Placing things (D_DeployableSetup, D_Deployable)
        ["DeployableName"] = ("Name while placing", "The name shown while you place it."),
        ["DeployableIcon"] = ("Icon while placing", "The picture shown while you place it."),
        ["DeployableBlueprint"] = ("Placed object", "What appears in the world once it's placed."),
        ["PreviewStaticMesh"] = ("Placing preview model", "The see-through model shown while you place it."),
        ["DeployedSound"] = ("Placed sound", "The sound when it's put down."),
        ["SupportsCustomRotation"] = ("Free rotation", "On = you can turn it to any angle while placing."),
        ["SnapBehaviour"] = ("Snapping", "How it snaps to other things while placing."),
        ["SnapSocketsOrTags"] = ("Snaps to", "Which sockets or kinds of thing it can snap onto."),
        ["SnapActorTags"] = ("Snap tags", "Tags other things look for to snap onto it."),
        ["UseSnapSocketRotation"] = ("Turn with the socket", "On = it takes the angle of what it snaps to."),
        ["IgnoreCollisionWhenSnapped"] = ("Overlap when snapped", "On = it can overlap things it's snapped to."),
        ["SnapToSurfaceNormal"] = ("Tilt to the ground", "On = it tilts to match the slope it's placed on."),
        ["MaxSurfaceSnapAngle"] = ("Steepest slope", "The steepest slope (degrees) it can be placed on. Higher = steeper ground is fine."),
        ["WorldPlacementType"] = ("Where it can go", "Where it may be placed, like on the ground or on buildings."),
        ["bCanAffectNavigation"] = ("Blocks creatures", "On = creatures walk around it. Off = they ignore it."),
        ["DeployPlacementOffset"] = ("Placing offset", "Moves it from where you aim while placing."),
        ["DeployCollisionExtentOffset"] = ("Placing space", "Changes how much room it needs to be placed."),
        ["AudioOcclusionAmount"] = ("Muffles sound", "How much it muffles sound through it. Higher = quieter behind it."),
        ["EffectedByWeather"] = ("Weather damages it", "On = storms can damage it when it isn't sheltered."),
        ["Variants"] = ("Variants", "Other versions of it you can switch between."),

        // Building stability (D_BuildingStability)
        ["BuildingTier"] = ("Building tier", "The material tier this stability belongs to (wood, stone, concrete…)."),
        ["MaxAnchoredStability"] = ("Stability on the ground", "Stability of a piece that touches the ground. Higher = you can build further out from it."),
        ["MaxHardStability"] = ("Strong stability", "Stability a piece keeps near solid support. Higher = sturdier."),
        ["HardStabilityMaxRange"] = ("Strong stability reach", "How far that strong stability reaches. Higher = further."),
        ["StabilityPassMultiplier"] = ("Stability passed on", "How much stability each piece passes to the next. Higher = longer spans before it falls."),
        ["MinimumUnstableStability"] = ("Collapses below", "A piece with less stability than this falls down. Lower = harder to make fall."),
        ["LowestGreenStability"] = ("Green from", "Stability shown green (solid) from this value up."),
        ["YellowStability"] = ("Yellow at", "Stability shown yellow (getting weak)."),
        ["HighestRedStability"] = ("Red up to", "Stability shown red (about to fall) up to this value."),
        ["Stability"] = ("Stability row", "Links to its building stability values."),
        ["PieceType"] = ("Piece type", "What kind of building piece it is (wall, floor, roof…)."),

        // Inventories (D_InventoryInfo)
        ["InventoryID"] = ("Inventory kind", "Which kind of inventory this is (backpack, storage, bench…)."),
        ["SlotTemplate"] = ("Slot kind", "The kind of slot it has, which decides what fits in it."),

        // Workshop (D_WorkshopItems)
        ["ResearchCost"] = ("Research cost", "What researching it costs, once (Ren, exotics, credits). Lower = cheaper."),
        ["ReplicationCost"] = ("Price per copy", "What each copy costs to buy from the workshop. Lower = cheaper."),
        ["Item"] = ("Item", "Which item this is."),

        // Talents and blueprints (D_Talents)
        ["TalentTree"] = ("Tree", "Which talent or blueprint tree it's in."),
        ["Position"] = ("Place in the tree", "Where it sits in the tree (X across, Y down)."),
        ["Size"] = ("Size in the tree", "How big its box is in the tree."),
        ["RequiredTalents"] = ("Needs first", "Talents you must have before this one."),
        ["RequiredRank"] = ("Needs rank", "The rank needed in the tree first."),
        ["RequiredLevel"] = ("Needs level", "The character level needed first. Lower = sooner."),
        ["Rewards"] = ("Gives", "What learning it gives you (stats, recipes, items)."),
        ["ExtraData"] = ("Unlocks", "The item or recipe it unlocks."),
        ["bDefaultUnlocked"] = ("Known from the start", "On = every character has it without spending a point."),
        ["TalentType"] = ("Talent type", "What kind of talent it is."),
        ["DrawMethodOverride"] = ("Line drawing", "How the line to the talents it needs is drawn."),

        // Buffs (D_ModifierStates)
        ["ModifierName"] = ("Buff name", "The buff's name players see."),
        ["ModifierDescription"] = ("Buff description", "The text shown for the buff."),
        ["ModifierIcon"] = ("Buff icon", "The buff's picture."),
        ["GrantedStats"] = ("Stats it gives", "The stats it gives while it's on."),
        ["MergeType"] = ("Getting it again", "What happens when you get it while it's still on (refresh, add time…)."),
        ["RemovedOnClick"] = ("Can be cancelled", "On = players can click the buff to remove it."),
        ["bEffectsNPCs"] = ("Affects creatures", "On = it also works on creatures and NPCs."),
        ["ModifierLifetimeAffectors"] = ("Lasts longer with", "Stats that make it last longer."),
        ["ModifierEffectivenessAffectors"] = ("Stronger with", "Stats that make it stronger."),

        // Eating and using (D_Consumable)
        ["Modifier"] = ("Buff", "The buff it gives."),
        ["Stats"] = ("Stats", "The stats it gives or changes."),
        ["Byproducts"] = ("Left over", "What's left after using it, like an empty bottle."),
        ["DescriptionText"] = ("Effect text", "The text describing what it does."),

        // Decay (D_Decayable)
        ["SpoiledItem"] = ("Spoils into", "What it turns into once it spoils. Empty = it just goes away."),

        // Power, water, generators
        ["GenerationRate"] = ("Makes per second", "How much it makes. Higher = more."),
        ["ResourceFlowRate"] = ("Flow rate", "How fast power or water moves through it. Higher = more."),
        ["AutoActivate"] = ("Starts by itself", "On = it starts working as soon as it can."),
        ["RequiresEnergy"] = ("Needs power", "On = it only works with power."),
        ["bRequiresShelter"] = ("Needs shelter", "On = it only works indoors or under cover."),

        // Farming (D_FarmingSeeds)
        ["OptimalBiomes"] = ("Grows best in", "The biomes where it grows best."),
        ["FatigueModifier"] = ("Tires the soil", "How much growing it wears out the soil. Lower = the soil lasts longer."),

        // Heat (D_Thermal)
        ["InnerRadius"] = ("Full heat range", "Within this distance you get the full warming or cooling. Higher = bigger."),
        ["OuterRadius"] = ("Heat reach", "How far its warming or cooling reaches at all. Higher = further."),
        ["TemperatureChange"] = ("Temperature change", "How much it warms (above 0) or cools (below 0) around it."),

        // Armour (D_Armour)
        ["ArmourType"] = ("Armour slot", "Which slot it goes in (head, chest, legs…)."),
        ["ArmourStats"] = ("Armour stats", "The stats it gives while worn."),
        ["ArmourMesh"] = ("Look", "The model players see when it's worn."),

        // Models (D_Meshable)
        ["ItemMesh"] = ("Model", "The model of the item."),
        ["EquipHandMesh"] = ("Model in hand", "The model when held."),
        ["DeployableActor"] = ("Placed model", "What's placed in the world."),

        // Stats themselves (D_Stats)
        ["Title"] = ("Title", "The stat's name players see."),
        ["PositiveDescription"] = ("Text when above 0", "What players read for a bonus. {0} is the number."),
        ["NegativeDescription"] = ("Text when below 0", "What players read for a penalty. {0} is the number."),
        ["StatCategory"] = ("Stat group", "Which group of stats it's listed under."),
        ["bShowStatOnModifiers"] = ("Show on buffs", "On = the stat is listed on buffs that change it."),

        // XP (D_ExperienceEvents)
        ["ExperienceGranted"] = ("XP given", "How much XP it gives. Higher = more."),
        ["EventDescription"] = ("XP text", "What players read for this XP."),
    };
}
