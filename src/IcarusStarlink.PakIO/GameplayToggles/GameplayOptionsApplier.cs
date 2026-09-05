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
    private const string TamesFile = "AI-D_Tames.json";
    private const string AlterationsFile = "Alterations-D_Alterations.json";
    private const string TalentsFile = "Talents-D_Talents.json";

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

        if (options.StacksMultiplier is > 0 || options.RemoveWeight)
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
        if (options.RemoveWeight)
        {
            ZeroExistingField(keyedTablesByFile, ItemableFile, "Weight", report, "Remove Weight");
        }
        if (options.SlotsMultiplier is > 0 and var slotsMultiplier)
        {
            // skipRow: HasSlotOverrides — see that method's own doc comment for why (Quickbar's
            // reserved Utility/Fists slots, Equipment/Space_Equipment/ArmourStand's fully-reserved
            // layouts all reference absolute positions that a blind scale would corrupt).
            ScaleExistingNumericField(keyedTablesByFile, InventoryFile, "StartingSlots", slotsMultiplier, minimum: 1, report, "Slots multiplier", skipRow: HasSlotOverrides);
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

    /// <summary>
    /// Real Data\Traits\D_Itemable.json rows without an explicit MaxStack field inherit MaxStack=1
    /// from the file's own struct-level Defaults block — invisible to a plain field-presence scan,
    /// and until now silently unaffected by this option even though the UI's own tooltip promises
    /// "e.g. 3x = triple every item's stack size" (an unqualified "every item"). Two real, independent
    /// community mods (Jimk72's "Increase Stacks": 431 rows, relentlessmoose's "rm_Stack_Sizes": 775
    /// rows) DO extend a new explicit MaxStack onto hundreds of these Defaults-only rows (deployable
    /// kits, furniture, trophies, weapon attachments) while never doing so for actual weapons/tools —
    /// matched here via IsWeaponOrTool since D_Itemable.json has no explicit item-category field of
    /// its own. Rows that already have an explicit MaxStack keep being scaled exactly as before.
    /// </summary>
    private static void ScaleStacksMultiplier(
        IDictionary<string, JsonObject> tables, IReadOnlyDictionary<string, JsonObject> originalFilesByFile, double multiplier, MergeReport report)
    {
        if (!tables.TryGetValue(ItemableFile, out var table))
        {
            return;
        }

        var defaultMaxStack = GetDefaultNumericValue(originalFilesByFile, ItemableFile, "MaxStack");

        var changes = new List<FieldChange>();
        foreach (var (itemName, rowValue) in table)
        {
            if (rowValue is not JsonObject row)
            {
                continue;
            }

            if (row["MaxStack"] is JsonValue currentValue && currentValue.TryGetValue<double>(out var current))
            {
                changes.Add(new FieldChange(ItemableFile, itemName, "MaxStack", currentValue, JsonValue.Create(Math.Max(1, (int)Math.Round(current * multiplier))), ValueSemantic.Scalar));
            }
            else if (row["MaxStack"] is null && defaultMaxStack is double defaultValue && !IsWeaponOrTool(row))
            {
                changes.Add(new FieldChange(ItemableFile, itemName, "MaxStack", null, JsonValue.Create(Math.Max(1, (int)Math.Round(defaultValue * multiplier))), ValueSemantic.Scalar));
            }
        }

        if (changes.Count == 0)
        {
            report.AddWarning($"Couldn't apply Stacks multiplier — no rows in {ItemableFile} have (or default to) a 'MaxStack' value. The game data may have changed since this option's field name was last confirmed.");
            return;
        }

        tables[ItemableFile] = TableApplier.Apply(table, changes, report);
    }

    /// <summary>
    /// See StackExclusionIconFolders' own doc comment for why a row's Icon path is the signal used
    /// here. A row with no Icon field of its own fails SAFE (treated as a weapon/tool, excluded)
    /// rather than fails open — confirmed against real data that this isn't just a theoretical edge
    /// case: Item_Hornet_Pistol, Item_Plant_Boss_Bow, and Item_Cat_Boss_Gauntlets (real, functional
    /// boss-reward weapons, each with a full row in Tools-D_FirearmData.json or Tools-D_ToolDamage.json)
    /// have no Icon field at all in Traits-D_Itemable.json, unlike every other real weapon/tool row.
    /// Failing open here would make a handful of unique boss-drop weapons stackable — exactly what
    /// this whole exclusion exists to prevent — whereas failing safe only costs a few ordinary,
    /// non-weapon Defaults-only items (also icon-less) not getting stack-boosted, a far smaller
    /// downside.
    /// </summary>
    private static bool IsWeaponOrTool(JsonObject row)
    {
        if (row["Icon"] is not JsonValue iconValue || !iconValue.TryGetValue<string>(out var icon))
        {
            return true;
        }

        foreach (var folder in StackExclusionIconFolders)
        {
            if (icon.Contains($"/Item_Icons/{folder}/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Real base-game rows with a non-empty SlotOverrides array reserve specific ABSOLUTE slot
    /// positions for a specific item type (e.g. Quickbar's own SlotOverrides pin position 10 to
    /// "Any_Utility" and 11 to "Player_Fists", right after its 10 regular slots) — those positions
    /// only make sense relative to the row's ORIGINAL StartingSlots. Blindly scaling StartingSlots
    /// without moving them would strand Quickbar's reserved slots in the middle of a larger bar
    /// instead of at the end, and would do the same (less visibly, since every position is already
    /// an override there) to Equipment/Space_Equipment/ArmourStand, whose entire slot count is
    /// nothing but SlotOverrides. Confirmed against real, independent community "increase slots"
    /// mods (Sarge_Deployable_Slots_Changes, Jimk72's own Increased Slots) — both hand-pick which
    /// rows to touch and neither one EVER includes Quickbar, Equipment, Space_Equipment, or
    /// ArmourStand, exactly this reasoning. This mirrors that same real-world convention instead of
    /// scaling every row indiscriminately.
    /// </summary>
    private static bool HasSlotOverrides(JsonObject row) => row["SlotOverrides"] is JsonArray { Count: > 0 };

    /// <summary>
    /// A player's real total slot count for many storage deployables/backpacks isn't just
    /// Inventory-D_InventoryInfo.json's own StartingSlots — crafting a Storage_1-4 alteration or a
    /// Carrying_Bonus_1/2 backpack alteration grants a flat "+N slots" bonus on top of it (see
    /// AlterationSlotBonusStatNames' own doc comment for the real stat keys/rows this covers).
    /// Without this, Slots Multiplier would only scale the base portion, so a player who's also
    /// crafted one of these alterations would see less than a clean ×N increase in their real total
    /// capacity. Each row's Stats object is scaled as a whole compound value (matching how
    /// GameplayOptionsFieldChangeGenerator already treats StatsGranted), not per-key FieldChanges,
    /// since TableApplier applies one FieldChange per (row, field) pair and Stats is the field here.
    /// </summary>
    private static void ScaleAlterationSlotBonuses(IDictionary<string, JsonObject> tables, double multiplier, MergeReport report)
    {
        if (!tables.TryGetValue(AlterationsFile, out var table))
        {
            return;
        }

        var changes = new List<FieldChange>();
        foreach (var (itemName, rowValue) in table)
        {
            if (rowValue is not JsonObject row || row["Stats"] is not JsonObject stats)
            {
                continue;
            }

            var newStats = stats.DeepClone()!.AsObject();
            var changedAny = false;
            foreach (var statName in AlterationSlotBonusStatNames)
            {
                if (stats[statName] is JsonValue currentValue && currentValue.TryGetValue<double>(out var current))
                {
                    newStats[statName] = JsonValue.Create(Math.Max(1, (int)Math.Round(current * multiplier)));
                    changedAny = true;
                }
            }

            if (changedAny)
            {
                changes.Add(new FieldChange(AlterationsFile, itemName, "Stats", stats, newStats, ValueSemantic.GenericCompound));
            }
        }

        if (changes.Count == 0)
        {
            report.AddWarning($"Couldn't apply Slots multiplier to Alteration upgrades — no rows in {AlterationsFile} have a slot-bonus stat. The game data may have changed since this option's field names were last confirmed.");
            return;
        }

        tables[AlterationsFile] = TableApplier.Apply(table, changes, report);
    }

    /// <summary>
    /// Same reasoning as ScaleAlterationSlotBonuses, for the "Extra Space" Workshop talent line's own
    /// flat deployable-storage bonus (see DeployableStorageTalentStatName's own doc comment). A
    /// talent's Rewards is an ARRAY (one entry per point spent in that talent), each with its own
    /// GrantedStats — every tier that grants this specific stat gets scaled, not just the first.
    /// </summary>
    private static void ScaleTalentDeployableStorageBonuses(IDictionary<string, JsonObject> tables, double multiplier, MergeReport report)
    {
        if (!tables.TryGetValue(TalentsFile, out var table))
        {
            return;
        }

        var changes = new List<FieldChange>();
        foreach (var (itemName, rowValue) in table)
        {
            if (rowValue is not JsonObject row || row["Rewards"] is not JsonArray rewards)
            {
                continue;
            }

            var newRewards = rewards.DeepClone()!.AsArray();
            var changedAny = false;
            for (var i = 0; i < newRewards.Count; i++)
            {
                if (newRewards[i] is not JsonObject newReward
                    || rewards[i] is not JsonObject originalReward
                    || originalReward["GrantedStats"] is not JsonObject originalGrantedStats
                    || originalGrantedStats[DeployableStorageTalentStatName] is not JsonValue currentValue
                    || !currentValue.TryGetValue<double>(out var current))
                {
                    continue;
                }

                (newReward["GrantedStats"] as JsonObject)![DeployableStorageTalentStatName] =
                    JsonValue.Create(Math.Max(1, (int)Math.Round(current * multiplier)));
                changedAny = true;
            }

            if (changedAny)
            {
                changes.Add(new FieldChange(TalentsFile, itemName, "Rewards", rewards, newRewards, ValueSemantic.GenericCompound));
            }
        }

        if (changes.Count == 0)
        {
            report.AddWarning($"Couldn't apply Slots multiplier to Workshop talents — no rows in {TalentsFile} have a '{DeployableStorageTalentStatName}' reward. The game data may have changed since this option's field name was last confirmed.");
            return;
        }

        tables[TalentsFile] = TableApplier.Apply(table, changes, report);
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
    /// Also scales QueryInputs (a separate, tag-based ingredient-cost structure — "consume 3 of any
    /// raw meat", not a specific item) alongside Inputs/ResourceInputs: 24 real rows (Cooked_Fish,
    /// the Butcher_*/Farmer_Plant_*/Fisher_*/Animal_*_Gruel families, etc.) rely on QueryInputs
    /// ENTIRELY, with a completely empty Inputs array — confirmed a real, shipped "everything is
    /// free" mod (laanp's FreeBuild) has this exact same gap itself (zeroes Inputs everywhere but
    /// leaves QueryInputs, e.g. Animal_Feed_Omni's Any_Raw_Meat/Any_Vegetable, untouched), so without
    /// this, Creative mode's promised "0 cost" isn't actually 0 for these recipes. QueryInputs
    /// elements use the same "Count" key as Inputs/ResourceInputs (only the reference-object key
    /// differs — "Query" vs "Element"), so the existing ZeroCountsInArray/ScaleCountsInArray helpers
    /// apply unmodified.
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
