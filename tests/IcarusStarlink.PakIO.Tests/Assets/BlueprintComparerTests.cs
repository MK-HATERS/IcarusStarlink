using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>Comparing a mod's copy of a game blueprint with the game's (shaped like Pickup and Move's player character).</summary>
public sealed class BlueprintComparerTests
{
    private static BlueprintSummary Summary(
        string[] parts, string[] variables, (string Name, string Code)[] functions, (string Key, string Value)[] defaults) =>
        new(parts.ToHashSet(), variables.ToHashSet(), functions.ToDictionary(f => f.Name, f => f.Code), defaults.ToDictionary(d => d.Key, d => d.Value));

    private static readonly BlueprintSummary Game = Summary(
        ["Function WeightUpdated", "Function GetCurrentProspectLocation", "SceneComponent Overlay_0"],
        ["RevNumber", "Weight"],
        [("WeightUpdated", "26:A"), ("GetCurrentProspectLocation", "85:B"), ("Drink", "40:C")],
        [("Default__BP_X_C.AltInteractHoldTime", "3.0"), ("Default__BP_X_C.SupportedColors", "[{\"R\":0.65}]"), ("Mesh.SkeletalMesh", "null")]);

    [Fact]
    public void Compare_ListsChangedSettingsAndFunctions_AndWhatEachSideHasAlone()
    {
        var mod = Summary(
            ["Function WeightUpdated", "Function GetCurrentProspectLocation", "Function Deployable_Pickup"],
            ["Weight", "StorageMoveFuelInventory"],
            [("WeightUpdated", "55:D"), ("GetCurrentProspectLocation", "44:E"), ("Drink", "40:C"), ("Deployable_Pickup", "12:F")],
            [("Default__BP_X_C.AltInteractHoldTime", "0.5"), ("Default__BP_X_C.SupportedColors", "[{\"R\":0.7}]")]);

        var difference = BlueprintComparer.Compare(Game, mod);

        Assert.Equal(["GetCurrentProspectLocation", "WeightUpdated"], difference.ChangedFunctions);
        Assert.Equal(["AltInteractHoldTime", "SupportedColors"], difference.Settings.Select(s => s.Name));
        Assert.Contains("function Deployable_Pickup", difference.OnlyInMod);
        Assert.Contains("variable StorageMoveFuelInventory", difference.OnlyInMod);
        Assert.Equal(["variable RevNumber", "Overlay_0"], difference.OnlyInGame);
        Assert.Equal(
            "changes 2 settings (AltInteractHoldTime 3 → 0.5, SupportedColors) and 2 functions (GetCurrentProspectLocation, WeightUpdated); "
            + "adds 2 parts (function Deployable_Pickup, variable StorageMoveFuelInventory); "
            + "doesn't have 2 things the game's copy has (variable RevNumber, Overlay_0)",
            difference.Describe());
    }

    [Fact]
    public void Compare_TheSameCopy_IsIdentical_AndEmptyEqualsMissing()
    {
        var difference = BlueprintComparer.Compare(Game, Game with { Defaults = Game.Defaults.Where(d => d.Key != "Mesh.SkeletalMesh").ToDictionary() });

        Assert.True(difference.IsIdentical);
        Assert.Equal("is identical", difference.Describe());
    }
}
