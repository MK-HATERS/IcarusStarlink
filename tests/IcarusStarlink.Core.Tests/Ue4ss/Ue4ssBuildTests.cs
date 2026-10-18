using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class Ue4ssBuildTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public Ue4ssBuildTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void TryParseDevAssetName_RealExperimentalAsset_ReadsVersionBuildAndCommit()
    {
        Assert.True(Ue4ssBuild.TryParseDevAssetName("UE4SS_v3.0.1-1181-g83330635.zip", out var version, out var build, out var commit));
        Assert.Equal(("3.0.1", 1181, "83330635"), (version, build, commit));
    }

    [Theory]
    [InlineData("UE4SS_v3.0.1.zip")]
    [InlineData("zDEV-UE4SS_v3.0.1-1181-g83330635.zip")]
    [InlineData("zCustomGameConfigs.zip")]
    [InlineData(null)]
    public void TryParseDevAssetName_OtherAssets_AreNotDevBuilds(string? name) =>
        Assert.False(Ue4ssBuild.TryParseDevAssetName(name, out _, out _, out _));

    [Theory]
    [InlineData("1c1a1497", "1c1a1497f942c707f47ba668db75b25e86f6c08a", true)]
    [InlineData("1C1A1497", "1c1a1497", true)]
    [InlineData("1c1a1497", "83330635", false)]
    [InlineData("1c1a", "1c1a1497", false)] // too short to trust
    [InlineData(null, "1c1a1497", false)]
    public void SameCommit_MatchesShortAndFullCommits(string? a, string? b, bool expected) =>
        Assert.Equal(expected, Ue4ssBuild.SameCommit(a, b));

    [Fact]
    public void Describe_NamesOfficialReleaseDevBuildsAndUnknowns()
    {
        Assert.Equal("3.0.1 (official release)", Ue4ssBuild.Describe("3.0.1", "d935b5b2"));
        Assert.Equal("3.0.1 dev build 1181", Ue4ssBuild.Describe("3.0.1", "83330635", 1181));
        Assert.Equal("3.0.1 dev build 1c1a1497", Ue4ssBuild.Describe("v3.0.1", "1c1a1497"));
        Assert.Equal("3.0.1", Ue4ssBuild.Describe("3.0.1", null));
    }

    [Fact]
    public void InstalledBuild_ReadsTheLog_ThenTheMarkerOnceTheStaleLogIsDropped()
    {
        File.WriteAllLines(Path.Combine(_folder, "UE4SS.log"),
            ["[2026-10-17 23:19:44.8458567] Console created", "[2026-10-17 23:19:44.8518280] UE4SS - v3.0.1 Beta #0 - Git SHA #1c1a1497"]);
        Assert.Equal(("3.0.1", "1c1a1497"), Ue4ssInstalledBuild.Read(_folder));

        Ue4ssInstalledBuild.RecordInstalled(_folder, "3.0.1", "83330635");

        Assert.False(File.Exists(Path.Combine(_folder, "UE4SS.log")));
        Assert.Equal(("3.0.1", "83330635"), Ue4ssInstalledBuild.Read(_folder));

        // UE4SS writes a fresh log on the next game start: that wins again.
        File.WriteAllLines(Path.Combine(_folder, "UE4SS.log"), ["UE4SS - v3.0.1 Beta #0 - Git SHA #83330635"]);
        Assert.Equal(("3.0.1", "83330635"), Ue4ssInstalledBuild.Read(_folder));
    }

    [Fact]
    public void InstalledBuild_UnknownBuild_RemovesTheMarker()
    {
        Ue4ssInstalledBuild.RecordInstalled(_folder, "3.0.1", "83330635");
        Ue4ssInstalledBuild.RecordInstalled(_folder, null, null);

        Assert.Equal((null, null), Ue4ssInstalledBuild.Read(_folder));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
