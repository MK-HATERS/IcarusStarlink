using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests.Pak;

/// <summary>An extracted pak's files moved to where the game sees them under Content — shapes taken from real Nexus paks.</summary>
public sealed class PakMountRestageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Restage_" + Guid.NewGuid().ToString("N"));

    public PakMountRestageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void Write(string relative) 
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private bool Has(string relative) => File.Exists(Path.Combine(_dir, relative));

    /// <summary>The mount point is the pak's own data: one that climbs back out after Content must not move files out of the folder.</summary>
    [Fact]
    public void AMountThatClimbsOutOfContent_StaysInsideTheFolder()
    {
        Write("evil.txt");

        var outside = PakMountPoints.RestageUnderGameContent(_dir, "../../../Icarus/Content/../../../../Escaped/");

        Assert.Equal(["evil.txt"], outside);
        Assert.True(Has("evil.txt"));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_dir, "..", "Escaped", "evil.txt"))));
    }

    /// <summary>BiggerStacks (Week-253): one table, mounted at its own folder.</summary>
    [Fact]
    public void DeepMount_PutsTheTableUnderData()
    {
        Write("D_Itemable.json");

        var outside = PakMountPoints.RestageUnderGameContent(_dir, "../../../Icarus/Content/data/Traits/");

        Assert.Empty(outside);
        Assert.True(Has(Path.Combine("data", "Traits", "D_Itemable.json")));
        Assert.False(Has("D_Itemable.json"));
    }

    /// <summary>Slow Day Night Cycle: mounted above Content, files under Icarus/Content/.</summary>
    [Fact]
    public void MountAboveContent_StripsTheIcarusContentPrefix()
    {
        Write(Path.Combine("Icarus", "Content", "BP", "Biomes", "Curves", "TimeScaleCurve.uasset"));

        var outside = PakMountPoints.RestageUnderGameContent(_dir, "../../../");

        Assert.Empty(outside);
        Assert.True(Has(Path.Combine("BP", "Biomes", "Curves", "TimeScaleCurve.uasset")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "Icarus")));
    }

    [Fact]
    public void FilesOutsideContent_StayPut_AndAreReported()
    {
        Write(Path.Combine("Engine", "Config", "Thing.ini"));
        Write(Path.Combine("Icarus", "Content", "data", "D_X.json"));

        var outside = PakMountPoints.RestageUnderGameContent(_dir, "../../../");

        Assert.Equal(["Engine/Config/Thing.ini"], outside);
        Assert.True(Has(Path.Combine("Engine", "Config", "Thing.ini")));
        Assert.True(Has(Path.Combine("data", "D_X.json")));
    }

    [Theory]
    [InlineData("../../../Icarus/Content/")]
    [InlineData("../../../Icarus/Content")]
    [InlineData(null)]
    public void ContentRootOrUnknown_ChangesNothing(string? mount)
    {
        Write(Path.Combine("data", "Traits", "D_Itemable.json"));

        Assert.Empty(PakMountPoints.RestageUnderGameContent(_dir, mount));
        Assert.True(Has(Path.Combine("data", "Traits", "D_Itemable.json")));
    }
}
