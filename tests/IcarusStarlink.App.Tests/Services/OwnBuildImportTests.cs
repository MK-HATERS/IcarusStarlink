using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// "IcarusStarlink.exe --import &lt;zip or folder&gt;" installs a build with no question, so only
/// that exact form may count. Windows starts Starlink as "exe" "%1" for every nxm:// link: a link
/// holding a raw quote, passed on by a launcher that doesn't encode it, splits into several
/// arguments, and an "--import" among them used to install someone else's files.
/// </summary>
public class OwnBuildImportTests
{
    [Fact]
    public void AnNxmLinkSplitByAQuote_IsNeverAnImport() =>
        Assert.Null(OwnBuildImport.PathIn(["nxm:", "--import", @"\\host\share\x.zip"]));

    [Fact]
    public void AnyNxmLinkAmongTheArguments_IsNeverAnImport() =>
        Assert.Null(OwnBuildImport.PathIn(["NXM://icarus/mods/1/files/2", "--import", @"C:\x.zip"]));

    [Fact]
    public void ImportThatIsNotTheFirstArgument_IsIgnored() =>
        Assert.Null(OwnBuildImport.PathIn(["--verbose", "--import", @"C:\x.zip"]));

    [Fact]
    public void TheDocumentedForm_GivesTheFullPath() =>
        Assert.Equal(@"C:\Builds\Bags.zip", OwnBuildImport.PathIn(["--import", @"C:\Builds\Bags.zip"]));

    [Fact]
    public void ImportWithNoPath_IsNotAnImport() =>
        Assert.Null(OwnBuildImport.PathIn(["--import", "  "]));
}
