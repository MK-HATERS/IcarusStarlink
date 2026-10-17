using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>How much can break when a mod replaces a game file (Pickup and Move's player character, Steadfast's building base…).</summary>
public sealed class ReplacedFileRiskTests
{
    [Theory]
    [InlineData("BP/Player/BP_IcarusPlayerCharacterSurvival", FileRisk.High, "the player character")]
    [InlineData("BP/Building/BP_Building_Base", FileRisk.High, "every building piece in the game builds on it")]
    [InlineData("BP/Objects/World/Items/Deployables/BP_DeployableBase", FileRisk.High, "every placeable item")]
    [InlineData("BP/Objects/World/Items/Deployables/BP_ProcessorBase", FileRisk.High, "every crafting bench and processor")]
    [InlineData("UI/Components/UMG_RevisionNumber", FileRisk.Medium, "always shown")]
    [InlineData("BP/Objects/World/Items/Deployables/Containers/BP_IceBox", FileRisk.Medium, "a game blueprint (IceBox)")]
    [InlineData("Assets/2DArt/UI/Windows/Item_Wood_Sticks", FileRisk.Low, "art or sound")]
    [InlineData("BP/Signs/M_SignWidgetText", FileRisk.Low, "art or sound (SignWidgetText)")]
    [InlineData("BP/Signs/UMG_Sign_Text_Window", FileRisk.Medium, "a game screen (Sign Text Window)")]
    [InlineData("AI/Curves/C_MountTalentGrowth", FileRisk.Low, "game numbers (MountTalentGrowth curve)")]
    [InlineData("ASS/DEP/SM_DEP_WaterPipe_SML", FileRisk.Low, "art or sound (DEP WaterPipe SML)")]
    public void Rate_ByWhatTheFileIs(string path, FileRisk risk, string what)
    {
        var rated = ReplacedFileRiskRater.Rate(path);

        Assert.Equal(risk, rated.Risk);
        Assert.Contains(what, rated.What);
    }

    [Fact]
    public void Assess_TheRiskiestFileSetsTheLevel_AndExplainsItInPlainWords()
    {
        var risk = ReplacedFileRiskRater.Assess(
            ["BP/Objects/World/Items/Deployables/Containers/BP_IceBox", "BP/Player/BP_IcarusPlayerCharacterSurvival"], [])!;

        Assert.Equal(FileRisk.High, risk.Level);
        Assert.Equal("High risk", risk.Label);
        Assert.StartsWith("Replaces 2 game files with its own copy:\n• the player character", risk.Explanation);
        Assert.Contains("building): BP_IcarusPlayerCharacterSurvival", risk.Explanation);
        Assert.Contains("try without this mod first", risk.Explanation);
    }

    [Fact]
    public void Assess_AClashMakesEvenArtMedium_AndNamesTheOtherMod()
    {
        var risk = ReplacedFileRiskRater.Assess(["Assets/T_Bestiary_ArcticMoa"], [new FileClash("Assets/T_Bestiary_ArcticMoa", ["Minimap"])])!;

        Assert.Equal(FileRisk.Medium, risk.Level);
        Assert.Contains("Minimap also replaces T_Bestiary_ArcticMoa with a different copy: only one can win", risk.Explanation);
    }

    [Fact]
    public void Assess_NothingReplaced_IsNoRisk()
    {
        Assert.Null(ReplacedFileRiskRater.Assess([], []));
        Assert.Equal(FileRisk.Low, ReplacedFileRiskRater.Assess(["Assets/T_Icon"], [])!.Level);
    }
}
