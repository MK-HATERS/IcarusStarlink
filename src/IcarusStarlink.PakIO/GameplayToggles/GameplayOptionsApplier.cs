using System.Text.Json.Nodes;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.GameplayToggles;

/// <summary>
/// Writes each enabled GameplayOptions toggle directly onto the already-merged keyed tables
/// RebuildService produces — matching classic IMM's own documented behavior of applying these as
/// a final pass over the merge result, not as another queued mod. Real field names/values below
/// are confirmed against the actual extracted game data and, where noted, classic IMM's own dev
/// changelog (not guessed) — see each private method's own comment for the specific source.
/// </summary>
public static class GameplayOptionsApplier
{
    private const string ItemableFile = "Traits-D_Itemable.json";
    private const string InventoryFile = "Inventory-D_InventoryInfo.json";
    private const string ProcessorRecipesFile = "Crafting-D_ProcessorRecipes.json";
    private const string ExtractorRecipesFile = "Crafting-D_ExtractorRecipes.json";
    private const string FirearmDataFile = "Tools-D_FirearmData.json";

    /// <summary>
    /// Which real data files each Category-2 (broadcast-to-every-row) option needs loaded —
    /// RebuildService uses this to make sure a file lands in the merged-table set even if no
    /// queued mod's own FieldChange touches it (options work with zero mods queued too, per the
    /// spec). Speed Boost/Player Boost/XP Boost/Disable Temperatures are NOT listed here anymore —
    /// they're real FieldChanges now (GameplayOptionsFieldChangeGenerator), so their own file need
    /// is already covered by resolvedChanges' own file list in RebuildService.
    /// </summary>
    public static IReadOnlySet<string> RequiredCurrentFiles(GameplayOptions options)
    {
        var files = new HashSet<string>();

        if (options.StacksMultiplier is > 0 || options.RemoveWeight)
        {
            files.Add(ItemableFile);
        }
        if (options.SlotsMultiplier is > 0)
        {
            files.Add(InventoryFile);
        }
        if (options.CraftCost != CraftCostReduction.Off || options.SpeedCraftingReductionPercent is > 0)
        {
            files.Add(ProcessorRecipesFile);
            files.Add(ExtractorRecipesFile);
        }
        if (options.UnlimitedAmmo)
        {
            files.Add(FirearmDataFile);
        }

        return files;
    }

    public static void Apply(GameplayOptions options, IDictionary<string, JsonObject> keyedTablesByFile, MergeReport report)
    {
        if (options.StacksMultiplier is > 0 and var stacksMultiplier && keyedTablesByFile.TryGetValue(ItemableFile, out var itemableForStacks))
        {
            ScaleExistingNumericField(itemableForStacks, "MaxStack", stacksMultiplier, minimum: 1);
        }
        if (options.RemoveWeight && keyedTablesByFile.TryGetValue(ItemableFile, out var itemableForWeight))
        {
            ZeroExistingField(itemableForWeight, "Weight");
        }
        if (options.SlotsMultiplier is > 0 and var slotsMultiplier && keyedTablesByFile.TryGetValue(InventoryFile, out var inventory))
        {
            ScaleExistingNumericField(inventory, "StartingSlots", slotsMultiplier, minimum: 1);
        }

        ApplyCraftCostReduction(options, keyedTablesByFile);
        ApplySpeedCrafting(options, keyedTablesByFile);

        if (options.UnlimitedAmmo && keyedTablesByFile.TryGetValue(FirearmDataFile, out var firearms))
        {
            SetExistingOrEveryRowField(firearms, "bUnlimitedAmmo", JsonValue.Create(true));
        }

        // Speed Boost/Player Boost/XP Boost/Disable Temperatures (all writing into the same
        // Base_Stats.StatsGranted field) are no longer applied here — they're real FieldChanges now
        // (GameplayOptionsFieldChangeGenerator), resolved through MergeEngine like any other mod's
        // change instead of a silent post-merge overwrite. See that class's own doc comment.
    }

    /// <summary>Field name/base values confirmed from real Data\Traits\D_Itemable.json (MaxStack, Weight sit on every item row alongside each other) — the exact multiplier is user-supplied since classic IMM never documented one for its own "Stacks Level 1/2".</summary>
    private static void ScaleExistingNumericField(JsonObject table, string fieldName, double multiplier, int minimum)
    {
        foreach (var (_, rowValue) in table)
        {
            if (rowValue is not JsonObject row || row[fieldName] is not JsonValue currentValue || !currentValue.TryGetValue<double>(out var current))
            {
                continue;
            }

            row[fieldName] = JsonValue.Create(Math.Max(minimum, (int)Math.Round(current * multiplier)));
        }
    }

    private static void ZeroExistingField(JsonObject table, string fieldName)
    {
        foreach (var (_, rowValue) in table)
        {
            if (rowValue is JsonObject row && row.ContainsKey(fieldName))
            {
                row[fieldName] = JsonValue.Create(0);
            }
        }
    }

    private static void SetExistingOrEveryRowField(JsonObject table, string fieldName, JsonNode? value)
    {
        foreach (var (_, rowValue) in table)
        {
            if (rowValue is JsonObject row)
            {
                row[fieldName] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// "Added reduce crafting cost %25/%50 to merge options... reduce all crafting bench recipes by
    /// %25/%50" plus "Added Creative mode to merge options... reduce all crafting bench costs to 0"
    /// per classic IMM's changelog — scales down (or, for Creative, zeroes) the ITEM inputs a recipe
    /// costs, never its outputs (that would be a free-item exploit, not a cost reduction). Creative
    /// needs its own literal-zero path rather than a 100% factor through ScaleCountsInArray, since
    /// that helper deliberately floors every result at 1 (never lets 25%/50% round all the way down
    /// to free) — Creative mode's whole point is actually reaching 0, so it can't reuse that clamp.
    /// </summary>
    private static void ApplyCraftCostReduction(GameplayOptions options, IDictionary<string, JsonObject> tables)
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

        foreach (var file in new[] { ProcessorRecipesFile, ExtractorRecipesFile })
        {
            if (!tables.TryGetValue(file, out var table))
            {
                continue;
            }

            foreach (var (_, rowValue) in table)
            {
                if (rowValue is not JsonObject row)
                {
                    continue;
                }

                if (options.CraftCost == CraftCostReduction.Creative)
                {
                    ZeroCountsInArray(row["Inputs"] as JsonArray, "Count");
                    ZeroCountsInArray(row["ResourceInputs"] as JsonArray, "RequiredUnits");
                }
                else
                {
                    ScaleCountsInArray(row["Inputs"] as JsonArray, "Count", factor);
                    ScaleCountsInArray(row["ResourceInputs"] as JsonArray, "RequiredUnits", factor);
                }
            }
        }
    }

    /// <summary>Creative mode's own zero-out — deliberately not routed through ScaleCountsInArray, which floors every result at a minimum of 1 and so can never actually reach free.</summary>
    private static void ZeroCountsInArray(JsonArray? array, string countField)
    {
        if (array is null)
        {
            return;
        }

        foreach (var element in array)
        {
            if (element is JsonObject obj && obj[countField] is JsonValue)
            {
                obj[countField] = JsonValue.Create(0);
            }
        }
    }

    /// <summary>
    /// "Changed how Speed Crafting effects the Crafting recipe. It now considers if there are
    /// resource inputs(Water, Milk, Biofuel) and resource outputs(Water, Milk, Biofuel) before
    /// modifying the speed" per classic IMM's changelog (its own most recent, deliberately-refined
    /// behavior — an older entry describes a cruder "sets craft time to 1 for all items", superseded
    /// by this). RequiredMillijoules is the real recipe field that governs process time (confirmed:
    /// real recipes have no separate Time/Duration field at all); the exact percentage is
    /// user-supplied since no specific number is documented for the current behavior.
    /// </summary>
    private static void ApplySpeedCrafting(GameplayOptions options, IDictionary<string, JsonObject> tables)
    {
        if (options.SpeedCraftingReductionPercent is not (> 0 and var percent))
        {
            return;
        }

        var factor = 1 - percent / 100.0;
        foreach (var file in new[] { ProcessorRecipesFile, ExtractorRecipesFile })
        {
            if (!tables.TryGetValue(file, out var table))
            {
                continue;
            }

            foreach (var (_, rowValue) in table)
            {
                if (rowValue is not JsonObject row || HasElements(row["ResourceInputs"] as JsonArray) || HasElements(row["ResourceOutputs"] as JsonArray))
                {
                    continue;
                }
                if (row["RequiredMillijoules"] is not JsonValue mjValue || !mjValue.TryGetValue<double>(out var current))
                {
                    continue;
                }

                row["RequiredMillijoules"] = JsonValue.Create(Math.Max(1, (int)Math.Round(current * factor)));
            }
        }
    }

    private static void ScaleCountsInArray(JsonArray? array, string countField, double factor)
    {
        if (array is null)
        {
            return;
        }

        foreach (var element in array)
        {
            if (element is not JsonObject obj || obj[countField] is not JsonValue value || !value.TryGetValue<double>(out var current))
            {
                continue;
            }

            obj[countField] = JsonValue.Create(Math.Max(1, (int)Math.Round(current * factor)));
        }
    }

    private static bool HasElements(JsonArray? array) => array is { Count: > 0 };
}
