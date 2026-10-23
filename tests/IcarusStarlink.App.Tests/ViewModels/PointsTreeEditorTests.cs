using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>"Change points per level": a row scales the game's own curve and previews it at the character's level.</summary>
public sealed class PointsTreeEditorTests
{
    private static GrowthCurveKey Key(float t, float v) => new(t, v, false, false, 0, 0);

    // C_PlayerTalentGrowth: 75 at 50, 90 at 60, flat after.
    private static readonly GrowthCurve GameTalents = new([Key(0, 0), Key(50, 75), Key(60, 90)], false, false);

    [Fact]
    public void Preview_ScalesTheGameCurve_AndKeepRisingCarriesItOn()
    {
        var editor = new PointsTreeEditor("TalentPointsPerLevel", "Talents", GameTalents, installed: null, () => 313, () => 16);
        Assert.Equal("1", editor.MultiplierText);
        Assert.False(editor.KeepRising);
        Assert.Equal("At level 313: 106 now → 106", editor.Preview);

        editor.MultiplierText = "2";
        Assert.Equal("At level 313: 106 now → 196", editor.Preview);

        editor.KeepRising = true; // 3 a level past 60 at ×2
        Assert.Equal(180 + 253 * 3 + 16, (int)editor.NewCurve.Evaluate(313) + 16);
    }

    /// <summary>A mod made before reopens with its own settings: its curve is the game's times 2, rising.</summary>
    [Fact]
    public void StartsFromWhatsInstalled()
    {
        var installed = new GrowthCurve([Key(0, 0), Key(50, 150), Key(60, 180)], false, true);
        var editor = new PointsTreeEditor("TalentPointsPerLevel", "Talents", GameTalents, installed, () => null, () => 0);

        Assert.Equal(2, editor.Multiplier);
        Assert.True(editor.KeepRising);
    }

    [Fact]
    public void ANonsenseMultiplier_IsRefused()
    {
        var editor = new PointsTreeEditor("TalentPointsPerLevel", "Talents", GameTalents, null, () => 60, () => 0) { MultiplierText = "0" };

        Assert.False(editor.IsValid);
        Assert.StartsWith("Enter a number", editor.Preview);
    }
}
