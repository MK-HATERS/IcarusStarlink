using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class SaveBestiaryEntryViewModelTests
{
    private static SaveBestiaryEntryViewModel Make(int pointsRequired = 1000, int currentPoints = 500) =>
        new("Forest_Wolf", "Forest Wolf", pointsRequired, isBoss: false, currentPoints, imagePath: null, onDirtyChanged: () => { });

    /// <summary>
    /// Regression guard: a negative typed value used to flow straight through to EffectivePoints,
    /// which SavesViewModel.ApplyBestiaryEdits' own `if (points > 0)` check then treated as
    /// "untracked" — silently deleting this creature's entire BestiaryTracking entry on Save instead
    /// of the edit simply being rejected, the way a too-high value already was.
    /// </summary>
    [Fact]
    public void OnPointsTextChanged_NegativeValue_FlooredToZero()
    {
        var entry = Make();

        entry.PointsText = "-50";

        Assert.Equal("0", entry.PointsText);
        Assert.Equal(0, entry.EffectivePoints);
    }

    [Fact]
    public void OnPointsTextChanged_TooHighValue_StillCeilingClampedAsBefore()
    {
        var entry = Make(pointsRequired: 1000);

        entry.PointsText = "5000";

        Assert.Equal("1000", entry.PointsText);
    }

    [Fact]
    public void OnPointsTextChanged_ValidValueWithinRange_NotClamped()
    {
        var entry = Make(pointsRequired: 1000, currentPoints: 500);

        entry.PointsText = "750";

        Assert.Equal("750", entry.PointsText);
        Assert.Equal(750, entry.EffectivePoints);
    }

    [Fact]
    public void OnPointsTextChanged_UnparsableText_LeftAsIs_EffectivePointsFallsBackToOriginal()
    {
        var entry = Make(currentPoints: 500);

        entry.PointsText = "not a number";

        Assert.Equal("not a number", entry.PointsText);
        Assert.Equal(500, entry.EffectivePoints);
    }
}
