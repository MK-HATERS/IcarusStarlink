using System.Text.Json.Nodes;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.GameplayToggles;

namespace IcarusStarlink.PakIO.Tests;

public class GameplayOptionsFieldChangeGeneratorTests : IDisposable
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private void WriteCharacterStats(string rowsJson)
    {
        var path = Path.Combine(_dataFolder, "Stats", "D_CharacterStartingStats.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{"RowStruct":"S","Defaults":{},"Rows":[{{rowsJson}}]}""");
    }

    /// <summary>The options' per-stat changes folded back into one StatsGranted list (stat key → value).</summary>
    private static JsonObject StatsOf(IReadOnlyList<FieldChange> changes)
    {
        var stats = new JsonObject();
        foreach (var change in changes.Where(c => c.CurrentFile == "Stats-D_CharacterStartingStats.json"))
        {
            Assert.True(StatEntries.TryParse(change.FieldName, out var field, out var key), change.FieldName);
            Assert.Equal("StatsGranted", field);
            Assert.Equal("Base_Stats", change.ItemName);
            Assert.False(change.IsFieldRemoved);
            stats[key] = change.NewValue?.DeepClone();
        }

        return stats;
    }

    [Fact]
    public void GenerateFixedFieldChanges_NoOptionsEnabled_ReturnsEmpty()
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{}}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(new GameplayOptions(), _dataFolder, new MergeReport());

        Assert.Empty(changes);
    }

    [Fact]
    public void GenerateFixedFieldChanges_DisableTemperatures_OverridesTheIsTemperatureEnabledStat()
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{"(Value=\"IsTemperatureEnabled_?\")":1}}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { DisableTemperatures = true }, _dataFolder, new MergeReport());

        var change = Assert.Single(changes);
        Assert.Equal("Stats-D_CharacterStartingStats.json", change.CurrentFile);
        Assert.Equal("Base_Stats", change.ItemName);
        Assert.Equal(StatEntries.Compose("StatsGranted", "(Value=\"IsTemperatureEnabled_?\")"), change.FieldName);
        Assert.Equal(ValueSemantic.Scalar, change.Semantic);
        Assert.Equal(1, (int)change.OriginalValue!);
        Assert.Equal(0, (int)change.NewValue!);
    }

    /// <summary>Only the stats the options set are changes; the game's other stats are left to the row, where they stay.</summary>
    [Fact]
    public void GenerateFixedFieldChanges_XpBoost_AddsExperienceStatWithoutRemovingExistingGrants()
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{"(Value=\"BaseMaximumHealth_+\")":300}}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { XpBoost = XpBoostLevel.Level3 }, _dataFolder, new MergeReport());

        var statsGranted = StatsOf(changes);
        Assert.Equal(1000, (int)statsGranted["(Value=\"BaseExperience_+%\")"]!);
        Assert.False(statsGranted.ContainsKey("(Value=\"BaseMaximumHealth_+\")"));

        var game = new JsonObject { ["Base_Stats"] = new JsonObject { ["StatsGranted"] = new JsonObject { ["(Value=\"BaseMaximumHealth_+\")"] = 300 } } };
        var row = TableApplier.Apply(game, changes)["Base_Stats"]!["StatsGranted"]!;
        Assert.Equal(300, (int)row["(Value=\"BaseMaximumHealth_+\")"]!);
        Assert.Equal(1000, (int)row["(Value=\"BaseExperience_+%\")"]!);
    }

    [Theory]
    [InlineData(XpBoostLevel.Level1, 200)]
    [InlineData(XpBoostLevel.Level2, 500)]
    [InlineData(XpBoostLevel.Level3, 1000)]
    public void GenerateFixedFieldChanges_XpBoost_EachLevelWritesItsOwnDocumentedPercent(XpBoostLevel level, int expectedPercent)
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{}}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { XpBoost = level }, _dataFolder, new MergeReport());

        var statsGranted = StatsOf(changes);
        Assert.Equal(expectedPercent, (int)statsGranted["(Value=\"BaseExperience_+%\")"]!);
    }

    [Fact]
    public void GenerateFixedFieldChanges_SpeedBoostAndPlayerBoostTogether_BothWriteIntoTheSameStatsGrantedWithoutClobberingEachOther()
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{}}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { SpeedBoost = BoostLevel.Level1, PlayerBoost = BoostLevel.Level1 }, _dataFolder, new MergeReport());

        var statsGranted = StatsOf(changes);
        Assert.Equal(455, (int)statsGranted["(Value=\"BaseMovementSpeed_+\")"]!);
        Assert.Equal(350, (int)statsGranted["(Value=\"BaseMaximumHealth_+\")"]!);
    }

    [Theory]
    [InlineData(BoostLevel.Level1, 25)]
    [InlineData(BoostLevel.Level2, 50)]
    public void GenerateFixedFieldChanges_SpeedBoost_AlsoGrantsTamedCreatureSpeedAndStamina(BoostLevel level, int expectedPercent)
    {
        // "Tamed Creature Movement Speed/Stamina" from classic IMM's own changelog — the real
        // StatsGranted key (confirmed against D_ArmourSetBonus.json/D_BestiaryData.json/
        // D_Talents.json/D_Equippable.json, all of which already grant this exact effect) needs a
        // "Base" prefix the changelog's own wording doesn't show.
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{}}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { SpeedBoost = level }, _dataFolder, new MergeReport());

        var statsGranted = StatsOf(changes);
        Assert.Equal(expectedPercent, (int)statsGranted["(Value=\"BaseTamedCreatureMovementSpeed_+%\")"]!);
        Assert.Equal(expectedPercent, (int)statsGranted["(Value=\"BaseTamedCreatureMaximumStamina_+%\")"]!);
    }

    [Fact]
    public void GenerateFixedFieldChanges_RemoveLevelCap_SetsBothLevelFieldsOnThePlayerRow()
    {
        var path = Path.Combine(_dataFolder, "Character", "D_CharacterGrowth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Player","MaxDisplayLevel":60,"MaxLevel":1000},{"Name":"AI","MaxDisplayLevel":60,"MaxLevel":1000}]}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { RemoveLevelCap = true }, _dataFolder, new MergeReport());

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.ItemName == "Player" && c.FieldName == "MaxDisplayLevel" && (int)c.NewValue! == 50000);
        Assert.Contains(changes, c => c.ItemName == "Player" && c.FieldName == "MaxLevel" && (int)c.NewValue! == 50000);
        // AI's own row is never touched — this option only ever emits a FieldChange for the Player
        // row (the merge pipeline's keyed-table shape can't express a Defaults-level change, which is
        // how the real reference mod, laanp-RealLevels, also reaches AI_Mounts/Settlement_Hub — see
        // GameplayOptions.RemoveLevelCap's own doc comment).
        Assert.DoesNotContain(changes, c => c.ItemName == "AI");
    }

    [Fact]
    public void GenerateFixedFieldChanges_RemoveLevelCapAlongsideSpeedBoost_BothChangesReturnedIndependently()
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{}}""");
        var growthPath = Path.Combine(_dataFolder, "Character", "D_CharacterGrowth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(growthPath)!);
        File.WriteAllText(growthPath, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Player","MaxDisplayLevel":60,"MaxLevel":1000}]}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { SpeedBoost = BoostLevel.Level1, RemoveLevelCap = true }, _dataFolder, new MergeReport());

        Assert.Equal(7, StatsOf(changes).Count);
        Assert.Equal(9, changes.Count);
        Assert.Contains(changes, c => c.CurrentFile == "Character-D_CharacterGrowth.json" && c.FieldName == "MaxDisplayLevel");
        Assert.Contains(changes, c => c.CurrentFile == "Character-D_CharacterGrowth.json" && c.FieldName == "MaxLevel");
    }

    [Fact]
    public void GenerateFixedFieldChanges_RemoveLevelCapWithMissingPlayerRow_AddsWarningAndReturnsEmpty()
    {
        var growthPath = Path.Combine(_dataFolder, "Character", "D_CharacterGrowth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(growthPath)!);
        File.WriteAllText(growthPath, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"AI"}]}""");
        var report = new MergeReport();

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { RemoveLevelCap = true }, _dataFolder, report);

        Assert.Empty(changes);
        Assert.Contains(report.Warnings, w => w.Contains("Player"));
    }

    [Fact]
    public void GenerateFixedFieldChanges_MissingBaseStatsRow_AddsWarningAndReturnsEmptyInsteadOfThrowing()
    {
        WriteCharacterStats("""{"Name":"Some_Other_Row"}""");
        var report = new MergeReport();

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { XpBoost = XpBoostLevel.Level1 }, _dataFolder, report);

        Assert.Empty(changes);
        Assert.Contains(report.Warnings, w => w.Contains("Base_Stats"));
    }

    [Fact]
    public void GenerateFixedFieldChanges_MissingCharacterStatsFile_AddsWarningAndReturnsEmptyInsteadOfThrowing()
    {
        // No WriteCharacterStats call at all — the file genuinely doesn't exist in this data folder.
        var report = new MergeReport();

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { SpeedBoost = BoostLevel.Level1 }, _dataFolder, report);

        Assert.Empty(changes);
        Assert.Contains(report.Warnings, w => w.Contains("Stats-D_CharacterStartingStats.json") || w.Contains("Stats/D_CharacterStartingStats.json"));
    }

    [Fact]
    public void GenerateFixedFieldChanges_AlongsideAQueuedModTouchingTheSameField_MergeEngineNowReportsARealConflict()
    {
        // The bug this whole generator exists to fix: GameplayOptionsApplier used to run as
        // a separate post-merge pass and silently overwrote whatever a queued mod set on
        // Base_Stats.StatsGranted — nothing in MergeEngine ever saw it as a conflict. Now that the
        // built-in options are a real FieldChange, MergeEngine.FindConflicts sees both candidates.
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{}}""");
        var builtIn = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { SpeedBoost = BoostLevel.Level1 }, _dataFolder, new MergeReport());
        var queuedModChange = new FieldChange(
            "Stats-D_CharacterStartingStats.json", "Base_Stats", "StatsGranted",
            OriginalValue: null, NewValue: new JsonObject { ["(Value=\"BaseMovementSpeed_+\")"] = 999 },
            ValueSemantic.GenericCompound);

        var conflicts = MergeEngine.FindConflicts(
            ["Some Queued Mod", "Built-in gameplay options"],
            [StatEntries.Split([queuedModChange]), builtIn]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(StatEntries.Compose("StatsGranted", "(Value=\"BaseMovementSpeed_+\")"), conflict.FieldName);
        Assert.Equal(2, conflict.Candidates.Count);
        Assert.Contains(conflict.Candidates, c => c.ModName == "Some Queued Mod");
        Assert.Contains(conflict.Candidates, c => c.ModName == "Built-in gameplay options");
    }

    /// <summary>
    /// Regression: the options' change carried every stat the queued mods set, so two mods
    /// disagreeing on a stat Player Boost never touches showed the options as a third candidate
    /// (and as the default winner, really holding the last mod's value).
    /// </summary>
    [Fact]
    public void GenerateFixedFieldChanges_TwoModsDisagreeOnAStatTheOptionsDontSet_OnlyThoseTwoConflict()
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{"(Value=\"BaseMaximumHealth_+\")":300,"(Value=\"BaseMaximumFallDamage_%\")":100}}""");
        static IReadOnlyList<FieldChange> ModStats(string json) => StatEntries.Split(
            [new("Stats-D_CharacterStartingStats.json", "Base_Stats", "StatsGranted", null, JsonNode.Parse(json), ValueSemantic.GenericCompound)]);
        var noFall = ModStats("""{"(Value=\"BaseMaximumFallDamage_%\")":0}""");
        var halfFall = ModStats("""{"(Value=\"BaseMaximumFallDamage_%\")":50}""");
        IReadOnlyList<FieldChange>[] earlier = [noFall, halfFall];

        var options = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { PlayerBoost = BoostLevel.Level1 }, _dataFolder, new MergeReport(), earlier);
        var conflicts = MergeEngine.FindConflicts(["WaypointTeleport", "Half fall", "Built-in gameplay options"], [noFall, halfFall, StatEntries.Split(options)]);

        var conflict = Assert.Single(conflicts, c => c.FieldName == StatEntries.Compose("StatsGranted", "(Value=\"BaseMaximumFallDamage_%\")"));
        Assert.Equal(["WaypointTeleport", "Half fall"], conflict.Candidates.Select(c => c.ModName));
        Assert.Equal(350, (int)StatsOf(options)["(Value=\"BaseMaximumHealth_+\")"]!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }
}
