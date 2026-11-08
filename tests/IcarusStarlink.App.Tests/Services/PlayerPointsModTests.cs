using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Writing the "Starlink Player Points" mod over its last copy: all or nothing, and no curve left behind that nothing uses.</summary>
public sealed class PlayerPointsModTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "PlayerPoints_" + Guid.NewGuid().ToString("N"));

    private string ExmodPath => Path.Combine(_folder, "Extracted Mods", $"{PlayerPointsMod.FileName}.EXMOD");

    private string Curve(string fileName) => Path.Combine(_folder, "Starlink", "PlayerPoints", fileName);

    public PlayerPointsModTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Extracted Mods"));
        Directory.CreateDirectory(Path.Combine(_folder, "Starlink", "PlayerPoints"));
        File.WriteAllText(ExmodPath, "the old mod");
        File.WriteAllBytes(Curve("C_A.uasset"), [1, 1, 1]);
        File.WriteAllBytes(Curve("C_A.uexp"), [2, 2, 2]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static ExmodPackageContents Contents(params string[] curveFiles) => new(
        new ExmodPackage
        {
            Name = PlayerPointsMod.DisplayName,
            Author = "Icarus Starlink",
            Version = "1",
            Description = "new",
            FileName = PlayerPointsMod.FileName,
        },
        [.. curveFiles.Select(f => new ExmodAssetEntry($"Starlink/PlayerPoints/{f}", [9, 9, 9]))]);

    /// <summary>
    /// Regression: the old curves were deleted before the new ones were written, so a write that
    /// failed (a locked file, a full disk) left the Library mod pointing the Player row at curves
    /// that were gone, and the next Merge &amp; Install built a broken row.
    /// </summary>
    [Fact]
    public void WriteWithRollback_WriteFails_PutsTheOldModBack()
    {
        File.WriteAllText(Path.Combine(_folder, "starlink-player-points.json"), "old settings");

        using (new FileStream(Curve("C_A.uexp"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => PlayerPointsMod.WriteWithRollback(_folder, Contents("C_B.uasset", "C_A.uasset", "C_A.uexp"), "new settings"));
        }

        Assert.Equal("the old mod", File.ReadAllText(ExmodPath));
        Assert.Equal([1, 1, 1], File.ReadAllBytes(Curve("C_A.uasset")));
        Assert.Equal([2, 2, 2], File.ReadAllBytes(Curve("C_A.uexp")));
        Assert.False(File.Exists(Curve("C_B.uasset")));
        Assert.Equal("old settings", File.ReadAllText(Path.Combine(_folder, "starlink-player-points.json")));
    }

    /// <summary>A tree put back to the game's points drops its curve from the mod: its files go too.</summary>
    [Fact]
    public void WriteWithRollback_Success_RemovesCurvesNoLongerUsed()
    {
        File.WriteAllBytes(Curve("C_B.uasset"), [3]);
        File.WriteAllBytes(Curve("C_B.uexp"), [4]);

        PlayerPointsMod.WriteWithRollback(_folder, Contents("C_A.uasset", "C_A.uexp"), "new settings");

        Assert.False(File.Exists(Curve("C_B.uasset")));
        Assert.False(File.Exists(Curve("C_B.uexp")));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Curve("C_A.uasset")));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Curve("C_A.uexp")));
        Assert.Equal("new settings", File.ReadAllText(Path.Combine(_folder, "starlink-player-points.json")));
        Assert.Contains(PlayerPointsMod.FileName, File.ReadAllText(ExmodPath));
    }
}
