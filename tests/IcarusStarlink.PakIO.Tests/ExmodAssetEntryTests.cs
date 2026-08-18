using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodAssetEntryTests
{
    [Fact]
    public void Equals_SameContentDifferentArrayInstances_AreEqual()
    {
        var a = new ExmodAssetEntry("readme.md", [1, 2, 3]);
        var b = new ExmodAssetEntry("readme.md", [1, 2, 3]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentContent_AreNotEqual()
    {
        var a = new ExmodAssetEntry("readme.md", [1, 2, 3]);
        var b = new ExmodAssetEntry("readme.md", [1, 2, 4]);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentPath_AreNotEqual()
    {
        var a = new ExmodAssetEntry("readme.md", [1, 2, 3]);
        var b = new ExmodAssetEntry("license.md", [1, 2, 3]);

        Assert.NotEqual(a, b);
    }
}
