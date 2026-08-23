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

    private static JsonObject StatsGrantedOf(FieldChange change) => Assert.IsType<JsonObject>(change.NewValue);

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
        Assert.Equal("StatsGranted", change.FieldName);
        Assert.Equal(0, (int)StatsGrantedOf(change)["(Value=\"IsTemperatureEnabled_?\")"]!);
    }

    [Fact]
    public void GenerateFixedFieldChanges_XpBoost_AddsExperienceStatWithoutRemovingExistingGrants()
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{"(Value=\"BaseMaximumHealth_+\")":300}}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { XpBoost = XpBoostLevel.Level3 }, _dataFolder, new MergeReport());

        var statsGranted = StatsGrantedOf(Assert.Single(changes));
        Assert.Equal(1000, (int)statsGranted["(Value=\"BaseExperience_+%\")"]!);
        // The base game's own existing grant survives — this FieldChange starts from real vanilla
        // StatsGranted, not an empty object, since it becomes the WHOLE field's value if it wins.
        Assert.Equal(300, (int)statsGranted["(Value=\"BaseMaximumHealth_+\")"]!);
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

        var statsGranted = StatsGrantedOf(Assert.Single(changes));
        Assert.Equal(expectedPercent, (int)statsGranted["(Value=\"BaseExperience_+%\")"]!);
    }

    [Fact]
    public void GenerateFixedFieldChanges_SpeedBoostAndPlayerBoostTogether_BothWriteIntoTheSameStatsGrantedWithoutClobberingEachOther()
    {
        WriteCharacterStats("""{"Name":"Base_Stats","StatsGranted":{}}""");

        var changes = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(
            new GameplayOptions { SpeedBoost = BoostLevel.Level1, PlayerBoost = BoostLevel.Level1 }, _dataFolder, new MergeReport());

        var statsGranted = StatsGrantedOf(Assert.Single(changes));
        Assert.Equal(455, (int)statsGranted["(Value=\"BaseMovementSpeed_+\")"]!);
        Assert.Equal(350, (int)statsGranted["(Value=\"BaseMaximumHealth_+\")"]!);
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
        // The bug this whole generator exists to fix: before Phase 1, GameplayOptionsApplier ran as
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
            [[queuedModChange], builtIn]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("StatsGranted", conflict.FieldName);
        Assert.Equal(2, conflict.Candidates.Count);
        Assert.Contains(conflict.Candidates, c => c.ModName == "Some Queued Mod");
        Assert.Contains(conflict.Candidates, c => c.ModName == "Built-in gameplay options");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }
}
