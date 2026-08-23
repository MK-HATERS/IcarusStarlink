using System.Text.Json.Nodes;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.GameplayToggles;

/// <summary>
/// Applies each enabled Category-2 ("broadcast to every row") GameplayOptions toggle as a final
/// pass over the already-merged keyed tables RebuildService produces — matching classic IMM's own
/// documented behavior of applying these after the queue's own mods merge, not as another queued
/// mod. Deliberately COMPOUNDING (new = current merged value × factor): reads whatever the queue's
/// mods already produced, so e.g. Stacks×2 doubles a custom item mod's own stack size too, not just
/// the base game's. Real field names/values below are confirmed against the actual extracted game
/// data and, where noted, classic IMM's own dev changelog (not guessed) — see each private method's
/// own comment for the specific source.
///
/// Each option builds its own FieldChange list (same model the rest of the merge pipeline uses)
/// and applies it via TableApplier.Apply instead of mutating the table directly — this makes
/// Category 1 and Category 2 consistent (one shared "how a change actually lands" code path) and,
/// concretely, gives a real defensive signal a raw-mutation loop never had: if a future game
/// update renames a field this relies on (e.g. Traits-D_Itemable.json stops having a MaxStack
/// field at all), zero rows match and a warning fires instead of the option just silently doing
/// nothing.
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
        if (options.StacksMultiplier is > 0 and var stacksMultiplier)
        {
            ScaleExistingNumericField(keyedTablesByFile, ItemableFile, "MaxStack", stacksMultiplier, minimum: 1, report, "Stacks multiplier");
        }
        if (options.RemoveWeight)
        {
            ZeroExistingField(keyedTablesByFile, ItemableFile, "Weight", report, "Remove Weight");
        }
        if (options.SlotsMultiplier is > 0 and var slotsMultiplier)
        {
            ScaleExistingNumericField(keyedTablesByFile, InventoryFile, "StartingSlots", slotsMultiplier, minimum: 1, report, "Slots multiplier");
        }

        ApplyCraftCostReduction(options, keyedTablesByFile, report);
        ApplySpeedCrafting(options, keyedTablesByFile, report);

        if (options.UnlimitedAmmo)
        {
            SetExistingOrEveryRowField(keyedTablesByFile, FirearmDataFile, "bUnlimitedAmmo", JsonValue.Create(true), report, "Unlimited Ammo");
        }

        // Speed Boost/Player Boost/XP Boost/Disable Temperatures (all writing into the same
        // Base_Stats.StatsGranted field) are no longer applied here — they're real FieldChanges now
        // (GameplayOptionsFieldChangeGenerator), resolved through MergeEngine like any other mod's
        // change instead of a silent post-merge overwrite. See that class's own doc comment.
    }

    /// <summary>Field name/base values confirmed from real Data\Traits\D_Itemable.json (MaxStack, Weight sit on every item row alongside each other) — the exact multiplier is user-supplied since classic IMM never documented one for its own "Stacks Level 1/2".</summary>
    private static void ScaleExistingNumericField(
        IDictionary<string, JsonObject> tables, string file, string fieldName, double multiplier, int minimum, MergeReport report, string optionName)
    {
        if (!tables.TryGetValue(file, out var table))
        {
            return;
        }

        var changes = new List<FieldChange>();
        foreach (var (itemName, rowValue) in table)
        {
            if (rowValue is not JsonObject row || row[fieldName] is not JsonValue currentValue || !currentValue.TryGetValue<double>(out var current))
            {
                continue;
            }

            var newValue = Math.Max(minimum, (int)Math.Round(current * multiplier));
            changes.Add(new FieldChange(file, itemName, fieldName, currentValue, JsonValue.Create(newValue), ValueSemantic.Scalar));
        }

        if (changes.Count == 0)
        {
            report.AddWarning($"Couldn't apply {optionName} — no rows in {file} have a '{fieldName}' field. The game data may have changed since this option's field name was last confirmed.");
            return;
        }

        tables[file] = TableApplier.Apply(table, changes, report);
    }

    private static void ZeroExistingField(IDictionary<string, JsonObject> tables, string file, string fieldName, MergeReport report, string optionName)
    {
        if (!tables.TryGetValue(file, out var table))
        {
            return;
        }

        var changes = new List<FieldChange>();
        foreach (var (itemName, rowValue) in table)
        {
            if (rowValue is JsonObject row && row[fieldName] is JsonValue currentValue)
            {
                changes.Add(new FieldChange(file, itemName, fieldName, currentValue, JsonValue.Create(0), ValueSemantic.Scalar));
            }
        }

        if (changes.Count == 0)
        {
            report.AddWarning($"Couldn't apply {optionName} — no rows in {file} have an explicit '{fieldName}' override to zero out. The game data may have changed since this option's field name was last confirmed.");
            return;
        }

        tables[file] = TableApplier.Apply(table, changes, report);
    }

    private static void SetExistingOrEveryRowField(
        IDictionary<string, JsonObject> tables, string file, string fieldName, JsonNode? value, MergeReport report, string optionName)
    {
        if (!tables.TryGetValue(file, out var table))
        {
            return;
        }

        var changes = new List<FieldChange>();
        foreach (var (itemName, rowValue) in table)
        {
            if (rowValue is JsonObject row)
            {
                changes.Add(new FieldChange(file, itemName, fieldName, row[fieldName], value?.DeepClone(), ValueSemantic.Scalar));
            }
        }

        if (changes.Count == 0)
        {
            report.AddWarning($"Couldn't apply {optionName} — {file} has no rows to set '{fieldName}' on. The game data may have changed since this option's field name was last confirmed.");
            return;
        }

        tables[file] = TableApplier.Apply(table, changes, report);
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
                if (rowValue is not JsonObject row)
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
            }

            if (changes.Count > 0)
            {
                matchedAnyRow = true;
                tables[file] = TableApplier.Apply(table, changes, report);
            }
        }

        if (!matchedAnyRow)
        {
            report.AddWarning("Couldn't apply Craft Cost — no crafting recipes with an 'Inputs' or 'ResourceInputs' array were found. The game data may have changed since this option's field names were last confirmed.");
        }
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
    /// "Changed how Speed Crafting effects the Crafting recipe. It now considers if there are
    /// resource inputs(Water, Milk, Biofuel) and resource outputs(Water, Milk, Biofuel) before
    /// modifying the speed" per classic IMM's changelog (its own most recent, deliberately-refined
    /// behavior — an older entry describes a cruder "sets craft time to 1 for all items", superseded
    /// by this). RequiredMillijoules is the real recipe field that governs process time (confirmed:
    /// real recipes have no separate Time/Duration field at all); the exact percentage is
    /// user-supplied since no specific number is documented for the current behavior.
    /// </summary>
    private static void ApplySpeedCrafting(GameplayOptions options, IDictionary<string, JsonObject> tables, MergeReport report)
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

            var changes = new List<FieldChange>();
            foreach (var (itemName, rowValue) in table)
            {
                if (rowValue is not JsonObject row || HasElements(row["ResourceInputs"] as JsonArray) || HasElements(row["ResourceOutputs"] as JsonArray))
                {
                    continue;
                }
                if (row["RequiredMillijoules"] is not JsonValue mjValue || !mjValue.TryGetValue<double>(out var current))
                {
                    continue;
                }

                var newValue = Math.Max(1, (int)Math.Round(current * factor));
                changes.Add(new FieldChange(file, itemName, "RequiredMillijoules", mjValue, JsonValue.Create(newValue), ValueSemantic.Scalar));
            }

            if (changes.Count > 0)
            {
                matchedAnyRow = true;
                tables[file] = TableApplier.Apply(table, changes, report);
            }
        }

        if (!matchedAnyRow)
        {
            report.AddWarning("Couldn't apply Speed Crafting — no eligible recipes with a 'RequiredMillijoules' field were found. The game data may have changed since this option's field name was last confirmed.");
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

    private static bool HasElements(JsonArray? array) => array is { Count: > 0 };
}
