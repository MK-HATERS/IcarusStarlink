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
    private const string CharacterStatsFile = "Stats-D_CharacterStartingStats.json";
    private const string BaseStatsRowName = "Base_Stats";

    /// <summary>Which real data files each enabled option needs loaded — RebuildService uses this to make sure a file lands in the merged-table set even if no queued mod's own FieldChange touches it (options work with zero mods queued too, per the spec).</summary>
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
        if (options.SpeedBoost != BoostLevel.Off || options.PlayerBoost != BoostLevel.Off
            || options.XpBoost != XpBoostLevel.Off || options.DisableTemperatures)
        {
            files.Add(CharacterStatsFile);
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

        ApplyCharacterStats(options, keyedTablesByFile, report);
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

    /// <summary>
    /// Speed Boost, Player Boost, XP Boost, and Disable Temperatures all write into the SAME row's
    /// StatsGranted (Stats\D_CharacterStartingStats.json, row "Base_Stats") — confirmed against the
    /// real file, and matching classic IMM's own changelog note that this exact row/field needed
    /// special per-key handling ("JSON in the StatsGranted section are now considered separate
    /// values... allows mods to add 1 stat or many stats and not effect... stats from other mods").
    /// Fetching the live StatsGranted object once and having each option write its own keys into it
    /// achieves the same thing here without needing that special-case anywhere else in the merge
    /// pipeline — these all run after the queue's own merge is already applied to real JsonNode
    /// objects, so there's no competing-priority conflict to resolve, just direct composition.
    /// </summary>
    private static void ApplyCharacterStats(GameplayOptions options, IDictionary<string, JsonObject> tables, MergeReport report)
    {
        var needsStats = options.SpeedBoost != BoostLevel.Off || options.PlayerBoost != BoostLevel.Off
            || options.XpBoost != XpBoostLevel.Off || options.DisableTemperatures;
        if (!needsStats || !tables.TryGetValue(CharacterStatsFile, out var table))
        {
            return;
        }

        if (table[BaseStatsRowName] is not JsonObject baseStatsRow)
        {
            report.AddWarning($"Couldn't apply gameplay options — '{BaseStatsRowName}' row not found in {CharacterStatsFile}.");
            return;
        }

        if (baseStatsRow["StatsGranted"] is not JsonObject statsGranted)
        {
            statsGranted = [];
            baseStatsRow["StatsGranted"] = statsGranted;
        }

        if (options.SpeedBoost != BoostLevel.Off)
        {
            ApplySpeedBoost(statsGranted, options.SpeedBoost);
        }
        if (options.PlayerBoost != BoostLevel.Off)
        {
            ApplyPlayerBoost(statsGranted, options.PlayerBoost);
        }
        if (options.XpBoost != XpBoostLevel.Off)
        {
            // "Boosts XP by %500" (Level 1) / "%1000" (Level 2) per the original two-tier changelog
            // entry; classic IMM's live app has since grown a third tier, "level 1 is %200, level 2
            // is %500 and Level 3 is %1000" (ver 2.4.4) — this now matches that current numbering.
            // No existing Experience-category grant on Base_Stats in the real data — this adds a new
            // one, using the same stat naming convention (BaseExperience_+%) the game's own stat
            // catalog defines.
            var percent = options.XpBoost switch
            {
                XpBoostLevel.Level1 => 200,
                XpBoostLevel.Level2 => 500,
                _ => 1000,
            };
            SetStat(statsGranted, "BaseExperience_+%", percent);
        }
        if (options.DisableTemperatures)
        {
            // The real base row already grants "IsTemperatureEnabled_?": 1 — overriding to 0 here.
            SetStat(statsGranted, "IsTemperatureEnabled_?", 0);
        }
    }

    /// <summary>Every value below is copied verbatim from classic IMM's own dev changelog ("11/27/25 Ver 2.3.7"), matched against the real base values on Base_Stats to confirm the field names. "Tamed Creature Movement Speed/Stamina" from the same changelog entry are omitted — no confirmed real field for those was found.</summary>
    private static void ApplySpeedBoost(JsonObject statsGranted, BoostLevel level)
    {
        var (baseSpeed, crouch, sprint, swim, swimSprint) = level == BoostLevel.Level1
            ? (455, 75, 250, 55, 175)
            : (600, 90, 300, 70, 200);

        SetStat(statsGranted, "BaseMovementSpeed_+", baseSpeed);
        SetStat(statsGranted, "CrouchMovementSpeedCoefficient_%", crouch);
        SetStat(statsGranted, "SprintMovementSpeedCoefficient_%", sprint);
        SetStat(statsGranted, "SwimMovementSpeedCoefficient_%", swim);
        SetStat(statsGranted, "SwimSprintMovementSpeedCoefficient_%", swimSprint);
    }

    /// <summary>
    /// Same changelog entry as ApplySpeedBoost. Omitted from Level 2: "Skinning Speed" and the
    /// visibility/QoL flags (Enable Highlight X Animals When ADS, Enable Automatic Wood Collection,
    /// Enable Map Can See World Bosses, Base Animal Highlight Distance) — no confirmed real field
    /// name was found for any of those (they don't follow the StatsGranted Base_Xxx_+/_%/_? naming
    /// convention the rest of this file relies on), so they're a known, documented gap rather than
    /// a guess.
    /// </summary>
    private static void ApplyPlayerBoost(JsonObject statsGranted, BoostLevel level)
    {
        SetStat(statsGranted, "BaseMaximumHealth_+", level == BoostLevel.Level1 ? 350 : 400);
        SetStat(statsGranted, "BaseMaximumStamina_+", level == BoostLevel.Level1 ? 250 : 300);
        SetStat(statsGranted, "BaseWeightCapacity_+", level == BoostLevel.Level1 ? 300 : 500);
        SetStat(statsGranted, "BaseFoodConsumptionPerHour_+", level == BoostLevel.Level1 ? 400 : 200);
        SetStat(statsGranted, "BaseWaterConsumptionPerHour_+", level == BoostLevel.Level1 ? 600 : 300);
        SetStat(statsGranted, "BaseOxygenConsumptionPerHour_+", level == BoostLevel.Level1 ? 200 : 100);
        SetStat(statsGranted, "BaseHealthRegenPerMinute_+", level == BoostLevel.Level1 ? 35 : 50);
        SetStat(statsGranted, "BaseMinimumFallDamageVelocity_+", 11000);
        SetStat(statsGranted, "BaseMaximumFallDamageVelocity_+", 22250);
        SetStat(statsGranted, "BaseColdResistance_%", level == BoostLevel.Level1 ? 30 : 60);
        SetStat(statsGranted, "BaseHeatResistance_%", level == BoostLevel.Level1 ? 30 : 60);
        SetStat(statsGranted, "BaseStaminaRegenPerMinute_+", level == BoostLevel.Level1 ? 3000 : 3800);
        SetStat(statsGranted, "BaseCollisionDamageResistance_%", 100);
        if (level == BoostLevel.Level2)
        {
            SetStat(statsGranted, "BaseOxygenConsumedPerStaminaUsed_+%", 2);
        }
    }

    // Every real stat value confirmed against the base game data and classic IMM's changelog is a
    // whole number (e.g. "300", never "300.0") — matching that here, rather than writing every
    // grant as a JSON double, keeps the output shape identical to what the game's own data already
    // looks like.
    private static void SetStat(JsonObject statsGranted, string statName, int value) =>
        statsGranted[$"(Value=\"{statName}\")"] = JsonValue.Create(value);
}
