using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>Game folders and files in plain words, for the fix window's missing-game-file hints.</summary>
public sealed class GameContentNamesTests
{
    [Theory]
    [InlineData("ASS/DEP/DEP_Turret_T4", "Deployables (benches, machines, things you place)")]
    [InlineData("/Game/ASS/ITM/Sickles/M_Sickle", "Items (tools, food, resources)")]
    [InlineData("ASS/ENV/TRE/Pine", "Environment, trees")]
    [InlineData("ass/env", "Environment (trees, rocks, plants)")]
    [InlineData("FMOD/Events/Creatures", "Sounds")]
    [InlineData("/Engine/BasicShapes/Cube", "Unreal Engine's own content")]
    public void AreaOf_NamesTheMostSpecificKnownFolder(string path, string expected) =>
        Assert.Equal(expected, GameContentNames.AreaOf(path));

    [Theory]
    [InlineData("SomeModFolder/Stuff")]
    [InlineData("ASSX/DEP")]
    [InlineData("EngineMaterials/Foo")]
    public void AreaOf_IsNullForAFolderItDoesntKnow(string path) =>
        Assert.Null(GameContentNames.AreaOf(path));

    [Fact]
    public void AreaOf_AssetsIsNotTakenForASS() =>
        Assert.Equal("Icons and 2D art", GameContentNames.AreaOf("Assets/2DArt/UI/Icons"));

    [Fact]
    public void FolderWithArea_ShortNameThenTheFolder()
    {
        Assert.Equal("Deployables (ASS/DEP)", GameContentNames.FolderWithArea("ASS/DEP"));
        Assert.Equal("Mods/Mine", GameContentNames.FolderWithArea("Mods/Mine"));
    }

    [Theory]
    [InlineData("SM_Cable", null, "mesh (3D model)")]
    [InlineData("MI_Cable", null, "material (how a surface looks)")]
    [InlineData("M_Cable", null, "material (how a surface looks)")]
    [InlineData("SK_Wolf", null, "animated model")]
    [InlineData("WBP_Inventory", null, "menu or HUD screen")]
    [InlineData("Cable", "StaticMesh", "mesh (3D model)")]
    [InlineData("BP_Thing", "AnimSequence", "animation")]
    public void KindOf_FromTheClassFirst_ThenTheName(string name, string? className, string expected) =>
        Assert.Equal(expected, GameContentNames.KindOf(name, className));

    [Fact]
    public void KindOf_IsNullWhenNothingSays() =>
        Assert.Null(GameContentNames.KindOf("Cable", "SomeNativeThing"));

    [Fact]
    public void Issue_SaysWhatTheFilesAreAndWhereInPlainWords()
    {
        var found = new MissingAssetReference(
            "ASS/DEP/Wires/SM_Cable.uasset", "/Game/ASS/DEP/M_ITM_CableHand",
            new Dictionary<string, string> { ["M_ITM_CableHand"] = "Material" },
            MissingReferenceKind.Moved, "/Game/ASS/ITM/M_ITM_CableHand", ["/Game/ASS/ITM/M_ITM_CableHand"]);

        var issue = Assert.Single(AssetReferenceIssues.From([found]));

        Assert.Contains("The mod's mesh (3D model) SM_Cable", issue.Why);
        Assert.Contains("the game's material (how a surface looks) M_ITM_CableHand", issue.Why);
        Assert.Contains("Deployables (ASS/DEP)", issue.Why);
        Assert.Contains("Items (ASS/ITM)", issue.Why);
    }
}
