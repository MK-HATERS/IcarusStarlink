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

    /// <summary>
    /// Reads the real base game data fresh (not a merged-table dictionary) so both RebuildService
    /// and MergeInstallViewModel's own conflict-preview path can call this identically — the
    /// latter never has a merged result to work from, only dataFolder.
    /// </summary>
    public static IReadOnlyList<FieldChange> GenerateFixedFieldChanges(GameplayOptions options, string dataFolder, MergeReport report)
    {
        var needsStats = options.SpeedBoost != BoostLevel.Off || options.PlayerBoost != BoostLevel.Off
            || options.XpBoost != XpBoostLevel.Off || options.DisableTemperatures;
        if (!needsStats)
        {
            return [];
        }

        var realRelativePath = CharacterStatsFile.Replace('-', '/');
        var basePath = Path.Combine(dataFolder, realRelativePath);
        if (!File.Exists(basePath))
        {
            report.AddWarning(
                $"Skipped built-in gameplay options — no matching file at '{realRelativePath}' in the extracted game data. "
                + "Run Update data folder again if the game has updated since your last one.");
            return [];
        }

        var fileJson = JsonNode.Parse(File.ReadAllText(basePath))!.AsObject();
        var keyed = DataTableJson.RowsToKeyedObject(fileJson, duplicateName => report.AddWarning(
            $"'{CharacterStatsFile}' has more than one row named '{duplicateName}' — only the last one was kept."));

        if (keyed[BaseStatsRowName] is not JsonObject baseStatsRow)
        {
            report.AddWarning($"Couldn't apply built-in gameplay options — '{BaseStatsRowName}' row not found in {CharacterStatsFile}.");
            return [];
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

        return [new FieldChange(
            CharacterStatsFile, BaseStatsRowName, StatsGrantedField,
            OriginalValue: originalStatsGranted, NewValue: newStatsGranted, ValueSemantic.GenericCompound,
            IsNewItem: false, IsFieldRemoved: false)];
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
