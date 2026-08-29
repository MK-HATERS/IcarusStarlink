using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodAssetCollisionCheckerTests
{
    private static ExmodPackageContents MakePackage(params (string RelativePath, byte[] Content)[] assets) => new(
        new ExmodPackage { Name = "Mod", Author = "A", Version = "1", Description = "D", FileName = "Mod", Rows = [] },
        [.. assets.Select(a => new ExmodAssetEntry(a.RelativePath, a.Content))]);

    [Fact]
    public void Check_NoSharedAssetPaths_ReturnsEmpty()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
            ("Mod B", MakePackage(("BP/Floor.uasset", [4, 5, 6]))),
        };

        var collisions = ExmodAssetCollisionChecker.Check(queued);

        Assert.Empty(collisions);
    }

    [Fact]
    public void Check_TwoModsShareAPathWithIdenticalContent_FlaggedAsIdentical()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
            ("Mod B", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
        };

        var collisions = ExmodAssetCollisionChecker.Check(queued);

        var collision = Assert.Single(collisions);
        Assert.Equal("BP/Wall.uasset", collision.RelativePath);
        Assert.True(collision.AreIdentical);
        Assert.Equal(["Mod A", "Mod B"], collision.ModNames);
    }

    [Fact]
    public void Check_TwoModsShareAPathWithDifferentContent_FlaggedAsNotIdentical()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
            ("Mod B", MakePackage(("BP/Wall.uasset", [9, 9, 9]))),
        };

        var collisions = ExmodAssetCollisionChecker.Check(queued);

        var collision = Assert.Single(collisions);
        Assert.False(collision.AreIdentical);
    }

    [Fact]
    public void Check_ThreeModsShareAPathTwoIdenticalOneDifferent_StillFlaggedAsNotIdentical()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
            ("Mod B", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
            ("Mod C", MakePackage(("BP/Wall.uasset", [9, 9, 9]))),
        };

        var collisions = ExmodAssetCollisionChecker.Check(queued);

        var collision = Assert.Single(collisions);
        Assert.False(collision.AreIdentical);
        Assert.Equal(["Mod A", "Mod B", "Mod C"], collision.ModNames);
    }

    [Fact]
    public void Check_PathCasingDiffersAcrossMods_StillTreatedAsTheSamePath()
    {
        // UnrealPak's own real staging/packing is case-insensitive on Windows — two mods
        // targeting "BP/Wall.uasset" and "bp/wall.uasset" really do collide on the same real file.
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
            ("Mod B", MakePackage(("bp/wall.uasset", [9, 9, 9]))),
        };

        var collisions = ExmodAssetCollisionChecker.Check(queued);

        Assert.Single(collisions);
    }

    [Fact]
    public void Check_TwoModsShareANonGameFileName_IsNotFlagged()
    {
        // Real gap found live: several unrelated real mods in the user's own library shared
        // generic filenames like "Readme.txt"/"Banner.PNG" — RebuildService.StageAssets no longer
        // even packs these into the merged pak (see GameAssetExtensions), so flagging a "collision"
        // on one would report a problem that can no longer actually happen.
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("Readme.txt", [1, 2, 3]))),
            ("Mod B", MakePackage(("Readme.txt", [9, 9, 9]))),
        };

        var collisions = ExmodAssetCollisionChecker.Check(queued);

        Assert.Empty(collisions);
    }

    [Fact]
    public void Check_OneModOnlyBundlesAnAsset_IsNotAConflict()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
        };

        var collisions = ExmodAssetCollisionChecker.Check(queued);

        Assert.Empty(collisions);
    }
}
