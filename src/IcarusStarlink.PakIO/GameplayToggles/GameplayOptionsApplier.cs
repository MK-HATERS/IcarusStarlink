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
public static partial class GameplayOptionsApplier
{
    private const string ItemableFile = "Traits-D_Itemable.json";
    private const string InventoryFile = "Inventory-D_InventoryInfo.json";
    private const string ProcessorRecipesFile = "Crafting-D_ProcessorRecipes.json";
    private const string ExtractorRecipesFile = "Crafting-D_ExtractorRecipes.json";
    private const string FirearmDataFile = "Tools-D_FirearmData.json";
    private const string TamesFile = "AI-D_Tames.json";
    private const string AlterationsFile = "Alterations-D_Alterations.json";
    private const string TalentsFile = "Talents-D_Talents.json";
    private const string DurableFile = "Traits-D_Durable.json";
    private const string ProspectForecastFile = "Weather-D_ProspectForecast.json";

    /// <summary>The game's own clear-sky forecast (WeatherPool NoWeather, three 66-minute Tier 0 steps), the one its own "clear weather" cheat switches a world to.</summary>
    private const string ClearForecastRowName = "ClearSky60";

    /// <summary>
    /// Forecasts No Weather leaves alone because a mission is built around the storm (real game data,
    /// 2026-10-28): IceStorm is the forecast of Tier2_Glacier_Expedition_0 ("EXPEDITION: Disrupt
    /// perma-storm") and the initial one of Great Hunt GH_IM_A2 ("... Disable to Stop the Weather
    /// Disruption"); Arena_IceMammoth_Blizzard is the Ice Mammoth arena's blizzard (pool
    /// Ice_Arena_Blizzard). Every other forecast draws from the general All_Weather pools.
    /// </summary>
    private static readonly string[] MissionForecastRowNames = ["IceStorm", "Arena_IceMammoth_Blizzard"];

    /// <summary>
    /// The real stat keys Alteration deployable/backpack upgrades grant as a flat "+N slots" bonus
    /// (confirmed against real Data\Alterations\D_Alterations.json: BaseGenericSlots_+ on
    /// Storage_1-4, BaseBackpackSlots_+ on Carrying_Bonus_1/2 — 6 rows total, nothing else in that
    /// whole 344-row table has a slot-shaped stat). Deliberately excludes mount/creature cargo
    /// stats (BaseMountCargoSlots_+, BaseMountHeavyCargoSlots_+ on Talents-D_Talents.json's own
    /// taming rewards) — a different system (mount capacity, not deployable storage), several of
    /// which are negative (a real speed-for-cargo trade-off talent), so blindly scaling those would
    /// both be out of scope for "more storage/backpack slots" and risk amplifying a deliberate
    /// downside instead of a bonus.
    /// </summary>
    private static readonly string[] AlterationSlotBonusStatNames =
    [
        "(Value=\"BaseGenericSlots_+\")",
        "(Value=\"BaseBackpackSlots_+\")",
    ];

    /// <summary>
    /// The one real stat key deployable-storage Workshop talents grant (confirmed against real
    /// Data\Talents\D_Talents.json: "CreatedDeployableStorageAlt_+" on Building_Storage_Increase and
    /// Building_Storage_Increase_0's own Rewards, each with multiple reward tiers — every value
    /// positive, nothing else in the whole 2227-row table uses this key). Matches exactly what the
    /// real Sarge_Deployable_Slots_Changes community mod scales alongside Inventory-D_InventoryInfo.json
    /// itself, confirmed by downloading and inspecting its own real EXMOD content.
    /// </summary>
    private const string DeployableStorageTalentStatName = "(Value=\"CreatedDeployableStorageAlt_+\")";

    /// <summary>
    /// Real Data\Traits\D_Itemable.json rows have no explicit item-category field, so a row's own
    /// Icon path is the one data-driven signal available to tell an actual weapon/tool apart from
    /// everything else (deployables, furniture, trophies, attachments, armour, etc.) — confirmed
    /// against the real extracted table: every row under these three icon folders is a genuine
    /// weapon or hand tool (e.g. Item_Wood_Bow, Item_Crossbow under "Weapons"; Item_Stone_Axe,
    /// Item_Metal_Pickaxe under "Tools"; Item_LegendaryWeapon_* under "LegendaryWeapons"), matching
    /// what two independent real "increase stacks" mods (Jimk72's, relentlessmoose's) never touch
    /// even while freely adding new MaxStack values to hundreds of other Defaults-only rows — see
    /// ScaleStacksMultiplier's own doc comment for the full picture.
    /// </summary>
    private static readonly string[] StackExclusionIconFolders = ["Weapons", "Tools", "LegendaryWeapons"];

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

        if (options.StacksMultiplier is > 0 || options.RemoveWeight || options.WeightReductionPercent is > 0)
        {
            files.Add(ItemableFile);
        }
        if (options.SlotsMultiplier is > 0)
        {
            // Alterations/Talents alongside the base Inventory table — see ScaleAlterationSlotBonuses/
            // ScaleTalentDeployableStorageBonuses' own doc comments for why a player's REAL total
            // slot count is base + these flat bonuses, not base alone.
            files.Add(InventoryFile);
            files.Add(AlterationsFile);
            files.Add(TalentsFile);
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
        if (options.TamingSpeedReductionPercent is > 0)
        {
            files.Add(TamesFile);
        }
        if (options.FreeItemRepair)
        {
            files.Add(DurableFile);
        }
        if (options.NoWeather)
        {
            files.Add(ProspectForecastFile);
        }

        return files;
    }

    private static readonly IReadOnlyDictionary<string, JsonObject> EmptyOriginalFiles = new Dictionary<string, JsonObject>();

    /// <summary>
    /// originalFilesByFile is each file's full pre-keying JsonObject (RebuildService's own
    /// "originalJson"/"original" — the same one it separately retains to preserve RowStruct/Defaults
    /// when writing a merged table back out) — optional (defaults to empty) since most options never
    /// need a file's Defaults block, only StacksMultiplier/SpeedCrafting's own Defaults-fallback do.
    /// RowsToKeyedObject already discards Defaults when building keyedTablesByFile, so this is the
    /// only way those two options can see it.
    /// </summary>
    public static void Apply(
        GameplayOptions options, IDictionary<string, JsonObject> keyedTablesByFile, MergeReport report,
        IReadOnlyDictionary<string, JsonObject>? originalFilesByFile = null)
    {
        var originals = originalFilesByFile ?? EmptyOriginalFiles;

        if (options.StacksMultiplier is > 0 and var stacksMultiplier)
        {
            ScaleStacksMultiplier(keyedTablesByFile, originals, stacksMultiplier, report);
        }
        if (options.RemoveWeight || options.WeightReductionPercent >= 100)
        {
            ZeroExistingField(keyedTablesByFile, ItemableFile, "Weight", report, "Remove Weight");
        }
        else if (options.WeightReductionPercent is > 0 and var lighterPercent)
        {
            // Same rows Remove Weight touches (an explicit Weight). Already-weightless items stay
            // weightless — the helper's minimum of 1 would otherwise give them weight.
            ScaleExistingNumericField(keyedTablesByFile, ItemableFile, "Weight", 1 - lighterPercent / 100.0, minimum: 1, report, "Lighter Items",
                skipRow: row => row["Weight"] is JsonValue weight && weight.TryGetValue<double>(out var w) && w <= 0);
        }
        if (options.SlotsMultiplier is > 0 and var slotsMultiplier)
        {
            // skipRow: HasSlotOverrides (see that method's own doc comment) OR IsSingleFixedEquipSlot
            // (see that one's own doc comment) — a row can be unsafe to scale for either reason.
            ScaleExistingNumericField(keyedTablesByFile, InventoryFile, "StartingSlots", slotsMultiplier, minimum: 1, report, "Slots multiplier", skipRow: row => HasSlotOverrides(row) || IsSingleFixedEquipSlot(row));
            ScaleAlterationSlotBonuses(keyedTablesByFile, slotsMultiplier, report);
            ScaleTalentDeployableStorageBonuses(keyedTablesByFile, slotsMultiplier, report);
        }

        ApplyCraftCostReduction(options, keyedTablesByFile, report);
        ApplySpeedCrafting(options, keyedTablesByFile, originals, report);

        if (options.UnlimitedAmmo)
        {
            SetExistingOrEveryRowField(keyedTablesByFile, FirearmDataFile, "bUnlimitedAmmo", JsonValue.Create(true), report, "Unlimited Ammo");
        }
        if (options.TamingSpeedReductionPercent is > 0 and var tamingPercent)
        {
            // TameDurationInSeconds — real field confirmed on every one of AI-D_Tames.json's own 29
            // creature rows (e.g. Moa: 900). No documented reduction from classic IMM, so the exact
            // percentage is user-supplied, same as Speed Crafting's own precedent.
            ScaleExistingNumericField(keyedTablesByFile, TamesFile, "TameDurationInSeconds", 1 - tamingPercent / 100.0, minimum: 1, report, "Faster Taming");
        }
        if (options.FreeItemRepair)
        {
            ApplyFreeItemRepair(keyedTablesByFile, report);
        }
        if (options.NoWeather)
        {
            ApplyNoWeather(keyedTablesByFile, report);
        }

        // Speed Boost/Player Boost/XP Boost/Disable Temperatures (all writing into the same
        // Base_Stats.StatsGranted field) are no longer applied here — they're real FieldChanges now
        // (GameplayOptionsFieldChangeGenerator), resolved through MergeEngine like any other mod's
        // change instead of a silent post-merge overwrite. See that class's own doc comment.
    }

    /// <summary>Field name/base values confirmed from real Data\Traits\D_Itemable.json (MaxStack, Weight sit on every item row alongside each other) — the exact multiplier is user-supplied since classic IMM never documented one for its own "Stacks Level 1/2".</summary>
    private static void ScaleExistingNumericField(
        IDictionary<string, JsonObject> tables, string file, string fieldName, double multiplier, int minimum, MergeReport report, string optionName,
        Func<JsonObject, bool>? skipRow = null)
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

            if (skipRow?.Invoke(row) == true)
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

    /// <summary>
    /// A file's own struct-level Defaults value for one field — the fallback every row without an
    /// explicit override of its own inherits at runtime (the same semantic ExmodFieldValidityChecker.
    /// BuildFieldKinds' own doc comment already documents elsewhere in this codebase: "preferring
    /// Defaults' own value... when a field appears in both"). RowsToKeyedObject discards Defaults
    /// when building the keyed table this class normally operates on, so this reads it from the
    /// separately-retained original (pre-keying) JsonObject instead.
    /// </summary>
    private static double? GetDefaultNumericValue(IReadOnlyDictionary<string, JsonObject> originalFilesByFile, string file, string fieldName)
    {
        if (originalFilesByFile.TryGetValue(file, out var originalFile)
            && originalFile["Defaults"] is JsonObject defaults
            && defaults[fieldName] is JsonValue defaultValue
            && defaultValue.TryGetValue<double>(out var value))
        {
            return value;
        }

        return null;
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
    /// Free Item Repair: empties ItemsForRepair on every Traits-D_Durable.json row that has one. That
    /// list (DurableData.ItemsForRepair, RepairData { Item, Amount }) is what a repair costs: 187 of
    /// the table's 236 rows have one (Stone_Axe: 1 Stone, Meta_Axe: 1 Meta_Repair_Item), the rest
    /// inherit the empty list from Defaults. Classic IMM's Free Item Repair ("Removes the
    /// requirements for repairing items") edits the same file and field. Only this one field is
    /// written: classic IMM 2.4.8 had to fix its own version for dropping "Destroyed_At_Zero": true
    /// (enemy turrets were never destroyed), and Max_Durability, Destroyed_At_Zero and
    /// NoRecipe_RequiredRecipeSet (which bench a repair needs) stay as they are.
    /// </summary>
    private static void ApplyFreeItemRepair(IDictionary<string, JsonObject> tables, MergeReport report)
    {
        if (!tables.TryGetValue(DurableFile, out var table))
        {
            return;
        }

        var changes = new List<FieldChange>();
        foreach (var (itemName, rowValue) in table)
        {
            if (rowValue is JsonObject row && row["ItemsForRepair"] is JsonArray { Count: > 0 } cost)
            {
                changes.Add(new FieldChange(DurableFile, itemName, "ItemsForRepair", cost, new JsonArray(), ValueSemantic.GenericCompound));
            }
        }

        if (changes.Count == 0)
        {
            report.AddWarning($"Couldn't apply Free Item Repair — no rows in {DurableFile} have an 'ItemsForRepair' list. The game data may have changed since this option's field name was last confirmed.");
            return;
        }

        tables[DurableFile] = TableApplier.Apply(table, changes, report);
    }

    /// <summary>
    /// No Weather: points every prospect forecast at the game's own clear-sky one (ClearSky60:
    /// WeatherPool NoWeather, whose only event is the empty NoWeather event in the NoBiome group,
    /// and three Tier 0 steps of 66 minutes). Prospects pick their weather from these forecast rows
    /// (D_ProspectList Forecast/InitialForecast: MidPeak, MostlyMild, Pulse, ...). Classic IMM's No
    /// Weather edited this file too (every Pattern step to Tier 0), and also emptied every
    /// D_WeatherEvents row; that table is left alone here, so a storm a quest starts itself still
    /// comes (the tutorial's Tutorial_Rain_1, which BP_QuestWeatherModifier adds directly), and the
    /// mission forecasts in MissionForecastRowNames keep their storms. The values are copied from
    /// the table's own ClearSky60 row, so a game update that changes it is followed.
    /// </summary>
    private static void ApplyNoWeather(IDictionary<string, JsonObject> tables, MergeReport report)
    {
        if (!tables.TryGetValue(ProspectForecastFile, out var table))
        {
            return;
        }

        if (table[ClearForecastRowName] is not JsonObject clear || clear["WeatherPool"] is not JsonNode clearPool || clear["Pattern"] is not JsonArray clearPattern)
        {
            report.AddWarning($"Couldn't apply No Weather — {ProspectForecastFile} has no '{ClearForecastRowName}' row with a WeatherPool and Pattern to copy. The game data may have changed since this option was last confirmed.");
            return;
        }

        var changes = new List<FieldChange>();
        foreach (var (forecastName, rowValue) in table)
        {
            if (rowValue is not JsonObject row
                || string.Equals(forecastName, ClearForecastRowName, StringComparison.OrdinalIgnoreCase)
                || MissionForecastRowNames.Contains(forecastName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            changes.Add(new FieldChange(ProspectForecastFile, forecastName, "WeatherPool", row["WeatherPool"], clearPool.DeepClone(), ValueSemantic.GenericCompound));
            changes.Add(new FieldChange(ProspectForecastFile, forecastName, "Pattern", row["Pattern"], clearPattern.DeepClone(), ValueSemantic.GenericCompound));
        }

        if (changes.Count == 0)
        {
            return;
        }

        tables[ProspectForecastFile] = TableApplier.Apply(table, changes, report);
    }
}
