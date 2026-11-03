using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>The game's own player curves (read from its paks), evaluated the way the engine does.</summary>
public sealed class GrowthCurveTests
{
    private static GrowthCurveKey Key(float t, float v) => new(t, v, IsCubic: false, IsConstant: false, 0, 0);

    // C_PlayerTalentGrowth: flat past its last key (default extrapolation).
    private static readonly GrowthCurve Talents = new([Key(0, 0), Key(50, 75), Key(60, 90)], LinearBefore: false, LinearAfter: false);

    // C_PlayerBlueprintGrowth: PostInfinityExtrap = Linear.
    private static readonly GrowthCurve Blueprints = new([Key(0, 0), Key(1, 4), Key(51, 179)], false, LinearAfter: true);

    // The last two keys of C_PlayerExperienceGrowth: PostInfinityExtrap = Linear, 144,000 XP a level past 51.
    private static readonly GrowthCurve Experience = new([Key(0, 0), Key(1, 2400), Key(50, 3890000), Key(51, 4034000)], false, LinearAfter: true);

    [Fact]
    public void FlatCurve_StopsAtItsLastKey()
    {
        Assert.Equal(82.5, Talents.Evaluate(55));
        Assert.Equal(90, Talents.Evaluate(313));
        Assert.Equal(0, Talents.SlopeAfter);
    }

    [Fact]
    public void LinearCurve_KeepsRisingPastItsLastKey()
    {
        Assert.Equal(3.5, Blueprints.SlopeAfter);
        Assert.Equal(179 + 262 * 3.5, Blueprints.Evaluate(313));
    }

    /// <summary>A real character: 41,867,472 XP is level 313 with Remove Level Cap, the game's own 1,000 or not.</summary>
    [Fact]
    public void LevelFor_FollowsTheXpCurve_AndStopsAtTheCap()
    {
        Assert.Equal(313, Experience.LevelFor(41_867_472, 50_000));
        Assert.Equal(60, Experience.LevelFor(41_867_472, 60));
        Assert.Equal(0, Experience.LevelFor(100, 50_000));
        Assert.Equal(51, Experience.LevelFor(4_034_000, 50_000));
    }

    /// <summary>The Characters tab's Level box: the least XP for a level, which LevelFor reads back as that level.</summary>
    [Fact]
    public void XpFor_IsTheLeastXpThatReachesTheLevel()
    {
        Assert.Equal(0, Experience.XpFor(0));
        Assert.Equal(2400, Experience.XpFor(1));
        Assert.Equal(4_034_000, Experience.XpFor(51));
        Assert.Equal(4_034_000 + 262L * 144_000, Experience.XpFor(313));
        foreach (var level in new[] { 1, 2, 25, 49, 50, 51, 52, 313, 1000 })
        {
            var xp = Experience.XpFor(level);
            Assert.Equal(level, Experience.LevelFor(xp, int.MaxValue));
            Assert.Equal(level - 1, Experience.LevelFor(xp - 1, int.MaxValue));
        }
    }

    [Fact]
    public void CurveAssetPath_IsTheCookedPackagesPath()
    {
        Assert.Equal("Data/Character/C_PlayerTalentGrowth.uasset",
            PlayerGrowthReader.CurveAssetPath("CurveFloat'/Game/Data/Character/C_PlayerTalentGrowth.C_PlayerTalentGrowth'"));
    }
}
