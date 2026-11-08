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
        Assert.Equal("BP/Wall", collision.RelativePath);
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

        Assert.False(Assert.Single(collisions).AreIdentical);
    }

    /// <summary>Regression: one game file is several parts on disk (.uasset, .uexp, .ubulk), and each part was its own "Same file" finding with the same text.</summary>
    [Fact]
    public void Check_TwoModsReplacingOneGameFile_IsOneCollision_NotOnePerFilePart()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]), ("BP/Wall.uexp", [4, 5, 6]))),
            ("Mod B", MakePackage(("BP\\Wall.uasset", [9, 9, 9]), ("bp/wall.UEXP", [8, 8, 8]))),
        };

        var collision = Assert.Single(ExmodAssetCollisionChecker.Check(queued));

        Assert.Equal("BP/Wall", collision.RelativePath);
        Assert.False(collision.AreIdentical);
        Assert.Equal(["Mod A", "Mod B"], collision.ModNames);
    }

    /// <summary>Regression: the same .uasset with a different .uexp showed only a ".uexp" line.</summary>
    [Fact]
    public void Check_SameUassetButDifferentUexp_IsOneCollisionThatDiffers()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]), ("BP/Wall.uexp", [4, 5, 6]))),
            ("Mod B", MakePackage(("BP/Wall.uasset", [1, 2, 3]), ("BP/Wall.uexp", [7, 7, 7]))),
        };

        var collision = Assert.Single(ExmodAssetCollisionChecker.Check(queued));

        Assert.Equal("BP/Wall", collision.RelativePath);
        Assert.False(collision.AreIdentical);
    }

    [Fact]
    public void Check_OneModShipsAPartTheOtherLeavesOut_IsNotIdentical()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]), ("BP/Wall.uexp", [4, 5, 6]))),
            ("Mod B", MakePackage(("BP/Wall.uasset", [1, 2, 3]))),
        };

        Assert.False(Assert.Single(ExmodAssetCollisionChecker.Check(queued)).AreIdentical);
    }

    [Fact]
    public void Check_EveryPartTheSame_IsIdentical()
    {
        var queued = new List<(string, ExmodPackageContents)>
        {
            ("Mod A", MakePackage(("BP/Wall.uasset", [1, 2, 3]), ("BP/Wall.uexp", [4, 5, 6]))),
            ("Mod B", MakePackage(("BP/Wall.uexp", [4, 5, 6]), ("BP/Wall.uasset", [1, 2, 3]))),
        };

        Assert.True(Assert.Single(ExmodAssetCollisionChecker.Check(queued)).AreIdentical);
    }

    [Fact]
    public void Check_TwoModsShareANonGameFileName_IsNotFlagged()
    {
        // Several unrelated real mods in one library share generic file names like
        // "Readme.txt"/"Banner.PNG"; RebuildService.StageFilesInQueueOrder doesn't pack these into
        // the merged pak (see GameAssetExtensions), so a "collision" on one can't happen.
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
