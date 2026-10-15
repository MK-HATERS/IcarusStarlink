using IcarusStarlink.PakIO.Pak;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Tests;

public sealed class ModLintsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"Lints_{Guid.NewGuid():N}");

    public ModLintsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A minimal pak tail: some index bytes, the 0x5A6F12E1 magic, then the version int — the shape every Unreal pak footer has.</summary>
    private string WriteFakePak(int version)
    {
        var path = Path.Combine(_dir, $"v{version}_P.pak");
        using var stream = File.Create(path);
        stream.Write(new byte[64]);
        stream.Write([0xE1, 0x12, 0x6F, 0x5A]);
        stream.Write(BitConverter.GetBytes(version));
        stream.Write(new byte[40]);
        return path;
    }

    [Fact]
    public void TryReadPakVersion_ReadsTheVersionAfterTheFooterMagic()
    {
        Assert.Equal(11, ModLints.TryReadPakVersion(WriteFakePak(11)));
        Assert.Equal(12, ModLints.TryReadPakVersion(WriteFakePak(12)));
    }

    [Fact]
    public void TryReadPakVersion_NotAPak_ReturnsNull()
    {
        var path = Path.Combine(_dir, "readme.txt");
        File.WriteAllText(path, "not a pak");
        Assert.Null(ModLints.TryReadPakVersion(path));
    }

    [Fact]
    public void CheckPakVersion_NewerThanIcarus_IsRefusedWithTheReason()
    {
        Assert.Null(ModLints.CheckPakVersion(WriteFakePak(11)));
        var finding = ModLints.CheckPakVersion(WriteFakePak(12));
        Assert.NotNull(finding);
        Assert.Contains("pak version 12", finding);
        Assert.Contains("4.27", finding);
    }

    [Fact]
    public void CheckForIoStoreContainers_FlagsUcasOrUtoc()
    {
        Assert.Null(ModLints.CheckForIoStoreContainers(["a/b.pak", "c.uasset"]));
        Assert.Contains("IoStore", ModLints.CheckForIoStoreContainers(["Mods/pakchunk99-Windows.utoc", "Mods/pakchunk99-Windows.ucas"]));
    }

    [Fact]
    public void CheckSplitAssetPairs_ReportsEachHalfMissingItsPartner()
    {
        var findings = ModLints.CheckSplitAssetPairs(["BP/Good.uasset", "BP/Good.uexp", "BP/Lonely.uasset", "BP/Orphan.uexp", "Readme.txt"]);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Contains("Lonely.uasset") && f.Contains(".uexp"));
        Assert.Contains(findings, f => f.Contains("Orphan.uexp") && f.Contains(".uasset"));
    }

    [Theory]
    [InlineData("../../../Icarus/Content/Mods/WirelessPower/", "Mods/WirelessPower/")]
    [InlineData("../../../Icarus/Content/", "")]
    [InlineData(null, "")]
    [InlineData("../../../Engine/Content/", "")]
    public void PakMountPoints_RelativePrefixUnderGameContent(string? mountPoint, string expected) =>
        Assert.Equal(expected, PakMountPoints.RelativePrefixUnderGameContent(mountPoint));

    [Fact]
    public void PakMountPoints_IsOutsideGameContent_OnlyForNonContentMounts()
    {
        Assert.True(PakMountPoints.IsOutsideGameContent("../../../Engine/Content/"));
        Assert.False(PakMountPoints.IsOutsideGameContent("../../../Icarus/Content/Mods/X/"));
        Assert.False(PakMountPoints.IsOutsideGameContent(null));
    }
}
