using System.Text.Json.Nodes;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.GameplayToggles;

/// <summary>Craft Cost (and Creative mode), leaving vendor trades alone, and Speed Crafting, with the recipe-count helpers.</summary>
public static partial class GameplayOptionsApplier
{
    /// <summary>
    /// "Added reduce crafting cost %25/%50 to merge options... reduce all crafting bench recipes by
    /// %25/%50" plus "Added Creative mode to merge options... reduce all crafting bench costs to 0"
    /// per classic IMM's changelog — scales down (or, for Creative, zeroes) the ITEM inputs a recipe
    /// costs, never its outputs (that would be a free-item exploit, not a cost reduction). Creative
    /// needs its own literal-zero path rather than a 100% factor through ScaleCountsInArray, since
    /// that helper deliberately floors every result at 1 (never lets 25%/50% round all the way down
    /// to free) — Creative mode's whole point is actually reaching 0, so it can't reuse that clamp.
    /// Also scales QueryInputs (a separate, tag-based ingredient-cost structure — "consume 3 of any
    /// raw meat", not a specific item) alongside Inputs/ResourceInputs: 24 real rows (Cooked_Fish,
    /// the Butcher_*/Farmer_Plant_*/Fisher_*/Animal_*_Gruel families, etc.) rely on QueryInputs
    /// ENTIRELY, with a completely empty Inputs array — confirmed a real, shipped "everything is
    /// free" mod (laanp's FreeBuild) has this exact same gap itself (zeroes Inputs everywhere but
    /// leaves QueryInputs, e.g. Animal_Feed_Omni's Any_Raw_Meat/Any_Vegetable, untouched), so without
    /// this, Creative mode's promised "0 cost" isn't actually 0 for these recipes. QueryInputs
    /// elements use the same "Count" key as Inputs/ResourceInputs (only the reference-object key
    /// differs — "Query" vs "Element"), so the existing ZeroCountsInArray/ScaleCountsInArray helpers
    /// apply unmodified. Trades with the NPC vendors are left alone (IsTradeRecipe): they are not
    /// crafting, and making them cheaper or free hands out Ren, trophies and boss heads for nothing.
    /// </summary>
    private static void ApplyCraftCostReduction(GameplayOptions options, IDictionary<string, JsonObject> tables, MergeReport report)
    {
        if (options.CraftCost == CraftCostReduction.Off)
        {
            return;
        }

        var factor = options.CraftCost switch
        {
            CraftCostReduction.TwentyFivePercent => 0.75,
            CraftCostReduction.FiftyPercent => 0.5,
            _ => 0.0,
        };

        var matchedAnyRow = false;
        foreach (var file in new[] { ProcessorRecipesFile, ExtractorRecipesFile })
        {
            if (!tables.TryGetValue(file, out var table))
            {
                continue;
            }

            var changes = new List<FieldChange>();
            foreach (var (itemName, rowValue) in table)
            {
                if (rowValue is not JsonObject row || IsTradeRecipe(row))
                {
                    continue;
                }

                var newInputs = options.CraftCost == CraftCostReduction.Creative
                    ? ZeroCountsInArray(row["Inputs"] as JsonArray, "Count")
                    : ScaleCountsInArray(row["Inputs"] as JsonArray, "Count", factor);
                if (newInputs is not null)
                {
                    changes.Add(new FieldChange(file, itemName, "Inputs", row["Inputs"], newInputs, ValueSemantic.GenericCompound));
                }

                var newResourceInputs = options.CraftCost == CraftCostReduction.Creative
                    ? ZeroCountsInArray(row["ResourceInputs"] as JsonArray, "RequiredUnits")
                    : ScaleCountsInArray(row["ResourceInputs"] as JsonArray, "RequiredUnits", factor);
                if (newResourceInputs is not null)
                {
                    changes.Add(new FieldChange(file, itemName, "ResourceInputs", row["ResourceInputs"], newResourceInputs, ValueSemantic.GenericCompound));
                }

                var newQueryInputs = options.CraftCost == CraftCostReduction.Creative
                    ? ZeroCountsInArray(row["QueryInputs"] as JsonArray, "Count")
                    : ScaleCountsInArray(row["QueryInputs"] as JsonArray, "Count", factor);
                if (newQueryInputs is not null)
                {
                    changes.Add(new FieldChange(file, itemName, "QueryInputs", row["QueryInputs"], newQueryInputs, ValueSemantic.GenericCompound));
                }
            }

            if (changes.Count > 0)
            {
                matchedAnyRow = true;
                tables[file] = TableApplier.Apply(table, changes, report);
            }
        }

        if (!matchedAnyRow)
        {
            report.AddWarning("Couldn't apply Craft Cost — no crafting recipes with an 'Inputs', 'ResourceInputs', or 'QueryInputs' array were found. The game data may have changed since this option's field names were last confirmed.");
        }
    }

    /// <summary>
    /// The recipe sets that are trades with an NPC vendor, not crafting (real game data, 2026-10-28:
    /// D_Processing's Hunter, Norex_NPC, Farmer_NPC, Fisher_NPC and Chef_NPC rows each have one of
    /// these as their DefaultRecipeSet). Vestige/Crops/Fish_Exchange turn vestiges, crops and fish
    /// into Ren (Common_Vestige: 1 vestige for 1 Ren), Biomass_Exchange turns Biomass into trophies
    /// and boss heads (28 rows), and Food_Shop sells food for Ren (Food_Soda: 10 Ren). Creative mode
    /// used to zero these, so every vendor gave currency and trophies for nothing; classic IMM 2.5.0
    /// fixed the same thing ("issues for QueryInputs from recipes that are quest related. Mainly the
    /// new exchange venders"). Any other set named *_Exchange counts too, so a new vendor the game
    /// or a mod adds the same way is left alone without a code change.
    /// </summary>
    private static readonly string[] TradeRecipeSetNames = ["Food_Shop"];

    private const string TradeRecipeSetSuffix = "_Exchange";

    /// <summary>True when any of the row's RecipeSets is a vendor trade (see TradeRecipeSetNames).</summary>
    private static bool IsTradeRecipe(JsonObject row)
    {
        if (row["RecipeSets"] is not JsonArray sets)
        {
            return false;
        }

        foreach (var set in sets)
        {
            if (set is JsonObject handle && handle["RowName"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name)
                && (name.EndsWith(TradeRecipeSetSuffix, StringComparison.OrdinalIgnoreCase)
                    || TradeRecipeSetNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Creative mode's own zero-out — deliberately not routed through ScaleCountsInArray, which floors every result at a minimum of 1 and so can never actually reach free. Returns a modified clone (null input in, null out) rather than mutating in place, matching TableApplier's own "the FieldChange carries the whole new value" model.</summary>
    private static JsonArray? ZeroCountsInArray(JsonArray? array, string countField)
    {
        if (array is null)
        {
            return null;
        }

        var result = array.DeepClone()!.AsArray();
        foreach (var element in result)
        {
            if (element is JsonObject obj && obj[countField] is JsonValue)
            {
                obj[countField] = JsonValue.Create(0);
            }
        }

        return result;
    }

    /// <summary>
    /// RequiredMillijoules is the real recipe field that governs process time (confirmed: real
    /// recipes have no separate Time/Duration field at all); the exact percentage is user-supplied
    /// since no specific number is documented for this behavior. Applies to every recipe with a
    /// RequiredMillijoules value, including ones with a Water/Milk/Biofuel ResourceInputs/
    /// ResourceOutputs entry — an older version of this method special-cased those out, matching a
    /// misread of classic IMM's own changelog ("It now considers if there are resource inputs...
    /// before modifying the speed"), but two independent, real "speed up crafting" mods (AgentKush's,
    /// TheLysdexicOne's) both simply halve RequiredMillijoules on these recipes exactly like any
    /// other, so the exclusion was dropped. Also falls back to the file's own Defaults
    /// [\"RequiredMillijoules\"] value for a row with no explicit override of its own (confirmed
    /// against real Data\Crafting\D_ProcessorRecipes.json: 817 of 2106 otherwise-eligible recipes —
    /// e.g. Stone_Pickaxe, Stone_Axe, Wood_Spear — inherit RequiredMillijoules=2500 from Defaults and
    /// were previously skipped entirely with zero effect; a real, actively-maintained mod, AgentKush's
    /// "Faster Crafting", explicitly sets RequiredMillijoules on 800 of those exact rows, confirming
    /// they're legitimate real-world targets) — writes the reduced value back as a new explicit
    /// override, same pattern SetExistingOrEveryRowField already uses for Unlimited Ammo.
    /// </summary>
    private static void ApplySpeedCrafting(
        GameplayOptions options, IDictionary<string, JsonObject> tables, IReadOnlyDictionary<string, JsonObject> originalFilesByFile, MergeReport report)
    {
        if (options.SpeedCraftingReductionPercent is not (> 0 and var percent))
        {
            return;
        }

        var factor = 1 - percent / 100.0;
        var matchedAnyRow = false;
        foreach (var file in new[] { ProcessorRecipesFile, ExtractorRecipesFile })
        {
            if (!tables.TryGetValue(file, out var table))
            {
                continue;
            }

            var defaultMillijoules = GetDefaultNumericValue(originalFilesByFile, file, "RequiredMillijoules");

            var changes = new List<FieldChange>();
            foreach (var (itemName, rowValue) in table)
            {
                if (rowValue is not JsonObject row)
                {
                    continue;
                }

                var originalValue = row["RequiredMillijoules"] as JsonValue;
                double current;
                if (originalValue is not null && originalValue.TryGetValue<double>(out current))
                {
                    // explicit row value — current already assigned above.
                }
                else if (originalValue is null && defaultMillijoules is double defaultValue)
                {
                    current = defaultValue;
                }
                else
                {
                    continue;
                }

                var newValue = Math.Max(1, (int)Math.Round(current * factor));
                changes.Add(new FieldChange(file, itemName, "RequiredMillijoules", originalValue, JsonValue.Create(newValue), ValueSemantic.Scalar));
            }

            if (changes.Count > 0)
            {
                matchedAnyRow = true;
                tables[file] = TableApplier.Apply(table, changes, report);
            }
        }

        if (!matchedAnyRow)
        {
            report.AddWarning("Couldn't apply Speed Crafting — no eligible recipes with (or defaulting to) a 'RequiredMillijoules' value were found. The game data may have changed since this option's field name was last confirmed.");
        }
    }

    /// <summary>Returns a modified clone (null input in, null out) rather than mutating in place — same reasoning as ZeroCountsInArray.</summary>
    private static JsonArray? ScaleCountsInArray(JsonArray? array, string countField, double factor)
    {
        if (array is null)
        {
            return null;
        }

        var result = array.DeepClone()!.AsArray();
        foreach (var element in result)
        {
            if (element is not JsonObject obj || obj[countField] is not JsonValue value || !value.TryGetValue<double>(out var current))
            {
                continue;
            }

            obj[countField] = JsonValue.Create(Math.Max(1, (int)Math.Round(current * factor)));
        }

        return result;
    }
}
