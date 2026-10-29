using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.GameplayToggles;

/// <summary>The Stacks multiplier (and which rows it leaves out) and the Slots multiplier's extra bonuses from alterations and talents.</summary>
public static partial class GameplayOptionsApplier
{
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
    /// A real bug found live: VisionSlot (the equipped-torch/light slot, StartingSlots=1) has no
    /// SlotOverrides — nothing to reserve when there's only one position — but scaling it to 2
    /// anyway (SlotsMultiplier=2) desynced the game's own single-fixed-icon UI for it, and crafting
    /// a replacement while the one real slot was already full silently lost the item instead of
    /// prompting a swap. Confirmed against the real, live Data\Inventory\D_InventoryInfo.json: every
    /// one of its ~70 StartingSlots=1 rows is this same shape — a single hardcoded equip point tied
    /// to one specific item category via a tag-restricted SlotTemplate (Weapon_AmmoSlot, Mount_Saddle,
    /// the per-limb *_Attachment rows, single-input crafting-station fuel/bait slots, etc.) — while
    /// every real multi-item storage row, tag-restricted or not (Backpack=24, Medicine_Bag=12,
    /// Container_Small=5, Pouch_Generic=6, ...), already starts above 1. No real "increase slots" mod
    /// would touch these either — there's no sensible "2 saddles" or "2 torches" to scale toward.
    /// Reads the row's CURRENT (pre-scale) StartingSlots, same field ScaleExistingNumericField itself
    /// is about to scale — this predicate is only ever wired to that one call site.
    /// </summary>
    private static bool IsSingleFixedEquipSlot(JsonObject row) =>
        row["StartingSlots"] is JsonValue currentValue && currentValue.TryGetValue<double>(out var current) && current == 1;

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
}
