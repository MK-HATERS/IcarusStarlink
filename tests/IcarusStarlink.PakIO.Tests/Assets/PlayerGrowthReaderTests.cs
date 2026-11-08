using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>Which D_CharacterGrowth row and curves the player's growth is read from: the mods' copy first, the game's own for the rest.</summary>
public sealed class PlayerGrowthReaderTests
{
    private const string XpCurvePath = "Data/Character/C_PlayerXP.uasset";
    private const string TalentCurvePath = "Data/Character/C_PlayerTalentGrowth.uasset";

    private static byte[] Table(int maxLevel) => System.Text.Encoding.UTF8.GetBytes($$"""
        {
          "RowStruct": "/Script/Icarus.CharacterGrowthData",
          "Defaults": { "MaxDisplayLevel": 60 },
          "Rows": [
            {
              "Name": "Player",
              "MaxLevel": {{maxLevel}},
              "ExperienceCurve": "CurveFloat'/Game/Data/Character/C_PlayerXP.C_PlayerXP'",
              "TalentPointsPerLevel": "CurveFloat'/Game/Data/Character/C_PlayerTalentGrowth.C_PlayerTalentGrowth'"
            }
          ]
        }
        """);

    private static GrowthCurve Curve(params (float Time, float Value)[] keys) =>
        new([.. keys.Select(k => new GrowthCurveKey(k.Time, k.Value, false, false, 0, 0))], false, true);

    [Fact]
    public void ModsTableWithStarlinksCap_WinsOverTheGamesAndKeepsTheGamesOwnCap()
    {
        var growth = PlayerGrowthReader.ReadRow("Player", Table(PlayerGrowth.RemovedLevelCap), Table(60), starlinkMerge: true, mods: null, new FakeGame());

        Assert.NotNull(growth);
        Assert.True(growth.TableFromMods);
        Assert.Equal(PlayerGrowth.RemovedLevelCap, growth.MaxLevel);
        Assert.Equal(60, growth.MaxDisplayLevel);
        Assert.Equal(60, growth.GameMaxLevel);
        Assert.True(growth.IsStarlinkLevelCap);
        Assert.True(growth.LevelCapChanged);
    }

    [Fact]
    public void SameCapWithoutStarlinksMerge_IsAnotherModsCap()
    {
        var growth = PlayerGrowthReader.ReadRow("Player", Table(PlayerGrowth.RemovedLevelCap), Table(60), starlinkMerge: false, mods: null, new FakeGame());

        Assert.NotNull(growth);
        Assert.False(growth.IsStarlinkLevelCap);
        Assert.True(growth.LevelCapChanged);
    }

    [Fact]
    public void NoModsTable_ReadsTheGamesRow()
    {
        var growth = PlayerGrowthReader.ReadRow("Player", modsTable: null, Table(60), starlinkMerge: true, mods: null, new FakeGame());

        Assert.NotNull(growth);
        Assert.False(growth.TableFromMods);
        Assert.Equal(60, growth.MaxLevel);
        Assert.False(growth.IsStarlinkLevelCap);
        Assert.False(growth.LevelCapChanged);
    }

    [Fact]
    public void RowInNeitherTable_IsNull()
    {
        Assert.Null(PlayerGrowthReader.ReadRow("AI_Mounts", Table(50000), Table(60), starlinkMerge: true, mods: null, new FakeGame()));
    }

    [Fact]
    public void CurvesNoModSupplies_ComeFromTheGame()
    {
        var xp = Curve((1, 0), (60, 1000));
        var talents = Curve((1, 1), (60, 60));
        var game = new FakeGame { [XpCurvePath] = xp, [TalentCurvePath] = talents };

        var growth = PlayerGrowthReader.ReadRow("Player", Table(PlayerGrowth.RemovedLevelCap), Table(60), starlinkMerge: true, mods: null, game);

        Assert.NotNull(growth);
        Assert.Same(xp, growth.Experience);
        Assert.Same(talents, growth.TalentPoints);
        Assert.Null(growth.BlueprintPoints);
        Assert.Empty(growth.CurvesFromMods);
        Assert.Equal(XpCurvePath, growth.GameCurvePathsByField["ExperienceCurve"]);
        Assert.Same(talents, growth.GameCurvesByField["TalentPointsPerLevel"]);
    }

    /// <summary>The game's paks: a curve by its Content-relative path, nothing else.</summary>
    private sealed class FakeGame : IBaseGameContentProvider
    {
        private readonly Dictionary<string, GrowthCurve> _curves = new(StringComparer.OrdinalIgnoreCase);

        public GrowthCurve this[string path]
        {
            set => _curves[path] = value;
        }

        public T? TryLoadExport<T>(string assetPath) where T : class => null;

        public TResult? TryLoadExportAndProject<T, TResult>(string assetPath, Func<T, TResult> project)
            where T : class where TResult : class =>
            _curves.GetValueOrDefault(assetPath) as TResult;
    }
}
