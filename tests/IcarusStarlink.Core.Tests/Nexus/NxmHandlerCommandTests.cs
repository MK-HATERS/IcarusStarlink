using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.Core.Tests.Nexus;

public class NxmHandlerCommandTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Black Tree Gaming Ltd\\Vortex\\Vortex.exe\" -d \"%1\"", "C:\\Program Files\\Black Tree Gaming Ltd\\Vortex\\Vortex.exe")]
    [InlineData("C:\\Tools\\nxmhandler.exe \"%1\"", "C:\\Tools\\nxmhandler.exe")]
    [InlineData("C:\\Tools\\handler.exe", "C:\\Tools\\handler.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExePathOf_QuotedOrPlain_FindsTheExe(string? command, string? expected) =>
        Assert.Equal(expected, NxmHandlerCommand.ExePathOf(command));

    [Fact]
    public void LaunchFor_VortexStyleCommand_PutsTheLinkWherePercentOneWas()
    {
        var launch = NxmHandlerCommand.LaunchFor("\"C:\\Vortex\\Vortex.exe\" -d \"%1\"", "nxm://skyrim/mods/1/files/2");

        Assert.Equal(("C:\\Vortex\\Vortex.exe", "-d \"nxm://skyrim/mods/1/files/2\""), launch);
    }

    [Fact]
    public void LaunchFor_CommandWithoutPercentOne_AddsTheLinkAsTheLastArgument()
    {
        var launch = NxmHandlerCommand.LaunchFor("\"C:\\Mgr\\Mgr.exe\"", "nxm://skyrim/mods/1/files/2");

        Assert.Equal(("C:\\Mgr\\Mgr.exe", "\"nxm://skyrim/mods/1/files/2\""), launch);
    }

    /// <summary>Two Starlink installs remembering each other would pass a link back and forth forever.</summary>
    [Theory]
    [InlineData("\"F:\\ModManager\\Icarus Mod Manager\\IcarusStarlink.App.exe\" \"%1\"", true)]
    [InlineData("\"C:\\Personal\\Stalker Pak\\StalkerModder.App.exe\" \"%1\"", false)]
    [InlineData(null, false)]
    public void IsStarlink_AnyStarlinkInstall(string? command, bool expected) =>
        Assert.Equal(expected, NxmHandlerCommand.IsStarlink(command));

    [Fact]
    public void ParseGames_CommasSpacesAndPastedLinks_LowerCaseDomainsWithoutIcarus() =>
        Assert.Equal(
            ["crimsondesert", "stalker2heartofchornobyl", "skyrimspecialedition"],
            NxmHandlerCommand.ParseGames("CrimsonDesert, https://www.nexusmods.com/stalker2heartofchornobyl/mods/5  icarus; skyrimspecialedition, crimsondesert"));

    [Fact]
    public void ChooseHandler_GameGivenToAnApp_GoesThere_OtherwiseToTheAppReplacedLast()
    {
        const string dmm = "\"C:\\Personal\\Crimson Desert DMM\\DMM.exe\" \"%1\"";
        const string stalker = "\"C:\\Stalker\\StalkerModder.App.exe\" \"%1\"";
        NxmOtherApp[] apps = [new(dmm, ["crimsondesert"]), new(stalker, ["stalker2heartofchornobyl"])];

        Assert.Equal(dmm, NxmHandlerCommand.ChooseHandler("CrimsonDesert", apps, fallback: stalker));
        Assert.Equal(stalker, NxmHandlerCommand.ChooseHandler("stalker2heartofchornobyl", apps, fallback: dmm));
        Assert.Equal(dmm, NxmHandlerCommand.ChooseHandler("skyrim", apps, fallback: dmm));
        Assert.Null(NxmHandlerCommand.ChooseHandler("skyrim", [], fallback: null));
    }

    [Fact]
    public void ChooseHandler_NeverAStarlink() =>
        Assert.Null(NxmHandlerCommand.ChooseHandler("skyrim", [], fallback: "\"D:\\Other\\IcarusStarlink.App.exe\" \"%1\""));

    [Theory]
    [InlineData("nxm://icarus/mods/290/files/1234", "icarus")]
    [InlineData("nxm://Stalker2HeartOfChornobyl/mods/1/files/2?key=a&expires=1", "stalker2heartofchornobyl")]
    [InlineData("https://www.nexusmods.com/icarus/mods/290", null)]
    [InlineData("not a url", null)]
    public void GameDomainOf_ReadsTheGameFromTheLink(string url, string? expected) =>
        Assert.Equal(expected, NxmHandlerCommand.GameDomainOf(url));
}
