using System.Text.Json.Nodes;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.GameplayToggles;

/// <summary>
/// Turns the "single fixed row" gameplay options (Speed Boost, Player Boost, XP Boost, Disable
/// Temperatures — all confirmed to write into the SAME row, Stats-D_CharacterStartingStats.json's
/// "Base_Stats", into its StatsGranted field) into a real FieldChange, so they become a genuine
/// MergeEngine participant instead of an invisible post-merge overwrite. Previously,
/// GameplayOptionsApplier.ApplyCharacterStats ran strictly after MultiFileMerger.Apply and blindly
/// set StatsGranted keys with zero visibility — a queued mod also setting e.g. BaseMovementSpeed_+
/// on Base_Stats was silently clobbered, with nothing in the conflict picker. Routing this through
/// MergeEngine.Merge/FindConflicts instead means: the same "built-in wins" default is preserved
/// (this FieldChange is appended last/highest-priority, same as before), but a real conflict is
/// now visible and the user can override it via the manual picker.
///
/// Deliberately NOT used for the "broadcast to every row" options (Stacks/Slots/Craft Cost/Speed
/// Crafting/Unlimited Ammo) — those stay GameplayOptionsApplier's own final pass. They're
/// compounding operations (new = current MERGED value × factor), and MergeEngine's "one absolute
/// value wins" model would silently break that compounding if routed through it the same way.
/// </summary>
public static class GameplayOptionsFieldChangeGenerator
{
    private const string CharacterStatsFile = "Stats-D_CharacterStartingStats.json";
    private const string BaseStatsRowName = "Base_Stats";
    private const string StatsGrantedField = "StatsGranted";

    private const string CharacterGrowthFile = "Character-D_CharacterGrowth.json";
    private const string PlayerGrowthRowName = "Player";

    /// <summary>
    /// Reads the real base game data fresh (not a merged-table dictionary) so both RebuildService
    /// and MergeInstallViewModel's own conflict-preview path can call this identically — the
    /// latter never has a merged result to work from, only dataFolder.
    /// </summary>
    public static IReadOnlyList<FieldChange> GenerateFixedFieldChanges(GameplayOptions options, string dataFolder, MergeReport report)
    {
        var changes = new List<FieldChange>();

        var needsStats = options.SpeedBoost != BoostLevel.Off || options.PlayerBoost != BoostLevel.Off
            || options.XpBoost != XpBoostLevel.Off || options.DisableTemperatures;
        if (needsStats && GenerateCharacterStatsChange(options, dataFolder, report) is { } statsChange)
        {
            changes.Add(statsChange);
        }

        if (options.RemoveLevelCap)
        {
            changes.AddRange(GenerateLevelCapChanges(dataFolder, report));
        }

        return changes;
    }

    private static FieldChange? GenerateCharacterStatsChange(GameplayOptions options, string dataFolder, MergeReport report)
    {
        var baseStatsRow = ReadKeyedRow(dataFolder, CharacterStatsFile, BaseStatsRowName, report);
        if (baseStatsRow is null)
        {
            return null;
        }

        // Starts from the real vanilla StatsGranted (not whatever a queued mod might also want
        // there) — if this FieldChange wins the field's conflict (the default, since it's the
        // highest-priority candidate), the result must be a complete, correct StatsGranted on its
        // own, the same way it always was when no other mod touched this field.
        var originalStatsGranted = baseStatsRow[StatsGrantedField] as JsonObject;
        var newStatsGranted = originalStatsGranted?.DeepClone() as JsonObject ?? [];

        if (options.SpeedBoost != BoostLevel.Off)
        {
            ApplySpeedBoost(newStatsGranted, options.SpeedBoost);
        }
        if (options.PlayerBoost != BoostLevel.Off)
        {
            ApplyPlayerBoost(newStatsGranted, options.PlayerBoost);
        }
        if (options.XpBoost != XpBoostLevel.Off)
        {
            // "Boosts XP by %500" (Level 1) / "%1000" (Level 2) per the original two-tier changelog
            // entry; classic IMM's live app has since grown a third tier, "level 1 is %200, level 2
            // is %500 and Level 3 is %1000" (ver 2.4.4) — this now matches that current numbering.
            var percent = options.XpBoost switch
            {
                XpBoostLevel.Level1 => 200,
                XpBoostLevel.Level2 => 500,
                _ => 1000,
            };
            SetStat(newStatsGranted, "BaseExperience_+%", percent);
        }
        if (options.DisableTemperatures)
        {
            // The real base row already grants "IsTemperatureEnabled_?": 1 — overriding to 0 here.
            SetStat(newStatsGranted, "IsTemperatureEnabled_?", 0);
        }

        return new FieldChange(
            CharacterStatsFile, BaseStatsRowName, StatsGrantedField,
            OriginalValue: originalStatsGranted, NewValue: newStatsGranted, ValueSemantic.GenericCompound,
            IsNewItem: false, IsFieldRemoved: false);
    }

    /// <summary>
    /// "Player" is the only row of Character-D_CharacterGrowth.json's 6 (Player/AI/AI_Mounts/
    /// AI_Pets/AI_SpeederBike/Settlement_Hub) a real community mod (laanp-RealLevels) touches — its
    /// own chosen value, 50000, is what this uses too, since classic IMM never documented one of
    /// its own (this option doesn't exist there). Real base values confirmed live: MaxDisplayLevel
    /// 60, MaxLevel 1000.
    /// </summary>
    private const int RemovedLevelCapValue = 50000;

    private static IReadOnlyList<FieldChange> GenerateLevelCapChanges(string dataFolder, MergeReport report)
    {
        var playerRow = ReadKeyedRow(dataFolder, CharacterGrowthFile, PlayerGrowthRowName, report);
        if (playerRow is null)
        {
            return [];
        }

        return
        [
            new FieldChange(
                CharacterGrowthFile, PlayerGrowthRowName, "MaxDisplayLevel",
                OriginalValue: playerRow["MaxDisplayLevel"], NewValue: JsonValue.Create(RemovedLevelCapValue), ValueSemantic.Scalar),
            new FieldChange(
                CharacterGrowthFile, PlayerGrowthRowName, "MaxLevel",
                OriginalValue: playerRow["MaxLevel"], NewValue: JsonValue.Create(RemovedLevelCapValue), ValueSemantic.Scalar),
        ];
    }

    /// <summary>Reads one real base-game file fresh and returns one named row, keyed the same way every other file in this pipeline is — null (with a warning already added to report) if the file or row doesn't exist, never throws.</summary>
    private static JsonObject? ReadKeyedRow(string dataFolder, string currentFile, string rowName, MergeReport report)
    {
        var realRelativePath = currentFile.Replace('-', '/');
        var basePath = Path.Combine(dataFolder, realRelativePath);
        if (!File.Exists(basePath))
        {
            report.AddWarning(
                $"Skipped built-in gameplay options — no matching file at '{realRelativePath}' in the extracted game data. "
                + "Run Update data folder again if the game has updated since your last one.");
            return null;
        }

        var fileJson = JsonNode.Parse(File.ReadAllText(basePath))!.AsObject();
        var keyed = DataTableJson.RowsToKeyedObject(fileJson, duplicateName => report.AddWarning(
            $"'{currentFile}' has more than one row named '{duplicateName}' — only the last one was kept."));

        if (keyed[rowName] is not JsonObject row)
        {
            report.AddWarning($"Couldn't apply built-in gameplay options — '{rowName}' row not found in {currentFile}.");
            return null;
        }

        return row;
    }

    /// <summary>
    /// Every value below is copied verbatim from classic IMM's own dev changelog ("11/27/25 Ver
    /// 2.3.7"), matched against the real base values on Base_Stats to confirm the field names.
    /// "Tamed Creature Movement Speed/Stamina" from the same changelog entry were originally
    /// omitted as "no confirmed real field found" — the field the changelog's own wording and
    /// D_TamedCreatureModifiers.json's own StatRequirement both point to
    /// ("TamedCreatureMovementSpeed_+%", no "Base" prefix) is a REQUIREMENT-check name that table
    /// uses to decide what to grant a tamed creature, not the actual StatsGranted key a player-side
    /// grant needs. The real granted-stat key was found by searching for where the game itself
    /// already grants this exact effect: D_ArmourSetBonus.json's "LM Beastmaster" set bonus,
    /// D_BestiaryData.json's tame-mastery unlocks, D_Talents.json, and D_Equippable.json's own
    /// modifiers all independently agree on "BaseTamedCreatureMovementSpeed_+%" /
    /// "BaseTamedCreatureMaximumStamina_+%" — the same Base-prefix convention every other stat in
    /// this method already follows.
    /// </summary>
    private static void ApplySpeedBoost(JsonObject statsGranted, BoostLevel level)
    {
        var (baseSpeed, crouch, sprint, swim, swimSprint, tamedCreature) = level == BoostLevel.Level1
            ? (455, 75, 250, 55, 175, 25)
            : (600, 90, 300, 70, 200, 50);

        SetStat(statsGranted, "BaseMovementSpeed_+", baseSpeed);
        SetStat(statsGranted, "CrouchMovementSpeedCoefficient_%", crouch);
        SetStat(statsGranted, "SprintMovementSpeedCoefficient_%", sprint);
        SetStat(statsGranted, "SwimMovementSpeedCoefficient_%", swim);
        SetStat(statsGranted, "SwimSprintMovementSpeedCoefficient_%", swimSprint);
        SetStat(statsGranted, "BaseTamedCreatureMovementSpeed_+%", tamedCreature);
        SetStat(statsGranted, "BaseTamedCreatureMaximumStamina_+%", tamedCreature);
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
