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

    /// <summary>Official releases are known by their tag's commit, so a 4.0.0 install reads as the release and a later commit as a dev build of it.</summary>
    [Fact]
    public void Describe_KnowsTheOfficial400Release_AndItsDevBuilds()
    {
        Assert.Equal("4.0.0 (official release)", Ue4ssBuild.Describe("4.0.0", "30afe5ff"));
        Assert.Equal("4.0.0 (official release)", Ue4ssBuild.Describe(null, Ue4ssBuild.Official400Commit));
        Assert.Equal("4.0.0 dev build 6", Ue4ssBuild.Describe("4.0.0", "ef15eba8", 6));
        Assert.True(Ue4ssBuild.IsOfficialRelease("30afe5fff94a94c2313e220b2002059a080da9a0"));
        Assert.True(Ue4ssBuild.IsOfficialRelease("d935b5b2"));
        Assert.False(Ue4ssBuild.IsOfficialRelease("ef15eba8"));
    }

    [Fact]
    public void TryParseAssetName_ReadsOfficialAndDevZips_NeverTheDeveloperZips()
    {
        Assert.True(Ue4ssBuild.TryParseAssetName("UE4SS_v4.0.0.zip", out var version, out var build, out var commit));
        Assert.Equal(("4.0.0", (int?)null, Ue4ssBuild.Official400Commit), (version, build, commit));

        Assert.True(Ue4ssBuild.TryParseAssetName("UE4SS_v3.0.1.zip", out version, out build, out commit));
        Assert.Equal(("3.0.1", (int?)null, Ue4ssBuild.OfficialReleaseCommit), (version, build, commit));

        Assert.True(Ue4ssBuild.TryParseAssetName("UE4SS_v4.0.0-6-gef15eba8.zip", out version, out build, out commit));
        Assert.Equal(("4.0.0", (int?)6, "ef15eba8"), (version, build, commit));

        Assert.False(Ue4ssBuild.TryParseAssetName("zDEV-UE4SS_v4.0.0.zip", out _, out _, out _));
        Assert.False(Ue4ssBuild.TryParseAssetName("zDEV-UE4SS_v4.0.0-6-gef15eba8.zip", out _, out _, out _));
        Assert.False(Ue4ssBuild.TryParseAssetName("UE4SS_v9.9.9.zip", out _, out _, out _)); // a release Starlink doesn't know the commit of
    }

    /// <summary>The version decides first (4.0.0 is newer than any 3.0.1 build), then the build number on the same version; only then is GitHub's commit date needed (Unknown here).</summary>
    [Theory]
    [InlineData("3.0.1", "83330635", 1181, "4.0.0", "30afe5ff", null, Ue4ssBuildOrder.Newer)]
    [InlineData("3.0.1", "aea3ed6e", 1221, "4.0.0", "30afe5ff", null, Ue4ssBuildOrder.Newer)]
    [InlineData("4.0.0", "30afe5ff", null, "3.0.1", "aea3ed6e", 1221, Ue4ssBuildOrder.Older)]
    [InlineData("3.0.1", null, null, "4.0.0", "ef15eba8", 6, Ue4ssBuildOrder.Newer)]
    [InlineData("4.0.0", "f54b7332", 1, "4.0.0", "ef15eba8", 6, Ue4ssBuildOrder.Newer)]
    [InlineData("4.0.0", "ef15eba8", 6, "4.0.0", "f54b7332", 1, Ue4ssBuildOrder.Older)]
    [InlineData("4.0.0", "30afe5ff", null, "4.0.0", "ef15eba8", 6, Ue4ssBuildOrder.Newer)] // a dev build comes after its release
    [InlineData("4.0.0", "abcdef12", null, "4.0.0", "30afe5ff", null, Ue4ssBuildOrder.Older)]
    [InlineData("4.0.0", "ef15eba8", null, "4.0.0", "EF15EBA8", 6, Ue4ssBuildOrder.Same)]
    [InlineData("4.0.0", "abcdef12", null, "4.0.0", "ef15eba8", 6, Ue4ssBuildOrder.Unknown)] // same version, one build number unknown
    [InlineData("4.0.0", null, null, "4.0.0", "ef15eba8", 6, Ue4ssBuildOrder.Unknown)]
    [InlineData(null, null, null, "4.0.0", "ef15eba8", 6, Ue4ssBuildOrder.Unknown)]
    public void Compare_VersionFirst_ThenBuildNumber(
        string? installedVersion, string? installedCommit, int? installedBuild,
        string? candidateVersion, string? candidateCommit, int? candidateBuild, Ue4ssBuildOrder expected) =>
        Assert.Equal(expected, Ue4ssBuild.Compare(installedVersion, installedCommit, installedBuild, candidateVersion, candidateCommit, candidateBuild));

    [Theory]
    [InlineData("3.0.1", 3)]
    [InlineData("v4.0.0", 4)]
    [InlineData("4", 4)]
    [InlineData("", null)]
    [InlineData("UE4SS", null)]
    [InlineData(null, null)]
    public void Major_ReadsTheFirstNumber(string? version, int? expected) => Assert.Equal(expected, Ue4ssBuild.Major(version));

    /// <summary>UE4SS 4 names itself the same way in UE4SS.log; the log and Starlink's marker both read back as 4.0.0.</summary>
    [Fact]
    public void InstalledBuild_ReadsA4xLogAndMarker()
    {
        File.WriteAllLines(Path.Combine(_folder, "UE4SS.log"),
            ["[2026-11-13 20:01:02.1234567] Console created", "[2026-11-13 20:01:02.1300000] UE4SS - v4.0.0 Beta #0 - Git SHA #30afe5f"]);
        Assert.Equal(("4.0.0", "30afe5f"), Ue4ssInstalledBuild.Read(_folder));

        Ue4ssInstalledBuild.RecordInstalled(_folder, "v4.0.0", "EF15EBA8");
        Assert.Equal(("4.0.0", "ef15eba8"), Ue4ssInstalledBuild.Read(_folder));
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
