using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Tests;

public class AssetPathGuardTests
{
    [Theory]
    [InlineData("CON.SomeMod")]
    [InlineData("con.SomeMod")]
    [InlineData("PRN")]
    [InlineData("COM1.zip")]
    public void SanitizeToSimpleFileName_ReservedDeviceNamePrefix_ProducesASafeName(string candidate)
    {
        var sanitized = AssetPathGuard.SanitizeToSimpleFileName(candidate);

        Assert.True(AssetPathGuard.IsSimpleFileName(sanitized), $"'{sanitized}' should itself be a safe simple file name.");
    }

    [Fact]
    public void SanitizeToSimpleFileName_NotReserved_LeftUnchanged()
    {
        Assert.Equal("Some Mod Name", AssetPathGuard.SanitizeToSimpleFileName("Some Mod Name"));
    }

    [Fact]
    public void SanitizeToSimpleFileName_EmptyAfterSanitizing_UsesGivenFallback()
    {
        Assert.Equal("download_x", AssetPathGuard.SanitizeToSimpleFileName("   ", "download_x"));
    }


    [Theory]
    [InlineData("Icarus/Content/Data/Crafting.uasset")]
    [InlineData("readme.md")]
    [InlineData("a/b/c/d.png")]
    public void IsSafeRelativePath_NormalPaths_ReturnsTrue(string path)
    {
        Assert.True(AssetPathGuard.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("../../../../Windows/System32/evil.dll")]
    [InlineData("a/../../b")]
    [InlineData("C:/Windows/System32/evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("readme.md:evil.exe")] // NTFS alternate-data-stream smuggling
    [InlineData("a/CON/b.txt")] // reserved device name as a path segment
    public void IsSafeRelativePath_TraversalOrAbsolutePaths_ReturnsFalse(string path)
    {
        Assert.False(AssetPathGuard.IsSafeRelativePath(path));
    }

    [Fact]
    public void ResolveWithinDirectory_TraversalPath_ThrowsFormatException()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests.Guard");

        Assert.Throws<FormatException>(() =>
            AssetPathGuard.ResolveWithinDirectory(baseDir, "../../evil.dll"));
    }

    [Fact]
    public void ResolveWithinDirectory_NormalPath_StaysInsideBaseDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests.Guard");

        var resolved = AssetPathGuard.ResolveWithinDirectory(baseDir, "a/b/file.txt");

        Assert.StartsWith(Path.GetFullPath(baseDir), resolved);
    }

    [Theory]
    [InlineData("Faster_Processors")]
    [InlineData("Take_Home_Tools_2x")]
    public void IsSimpleFileName_PlainNames_ReturnsTrue(string name)
    {
        Assert.True(AssetPathGuard.IsSimpleFileName(name));
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("a:b")] // Windows drive qualifier — makes Path.Combine discard the base directory
    [InlineData("CON")] // reserved Windows device name
    [InlineData("com1")] // case-insensitive
    [InlineData("NUL.txt")] // reserved regardless of extension
    [InlineData("con.anything")]
    public void IsSimpleFileName_PathLikeOrReservedNames_ReturnsFalse(string name)
    {
        Assert.False(AssetPathGuard.IsSimpleFileName(name));
    }

    [Theory]
    [InlineData("Faster_Processors.uasset")] // a dot in an otherwise-normal filename is fine
    [InlineData("NULLIFY")] // starts with a reserved name but isn't one
    public void IsSimpleFileName_NamesThatOnlyResembleReservedOnes_ReturnsTrue(string name)
    {
        Assert.True(AssetPathGuard.IsSimpleFileName(name));
    }

    [Fact]
    public void IsSimpleFileName_DriveQualifiedName_DoesNotSilentlyEscapeBaseDirectory()
    {
        // Regression for the exact failure mode: Path.Combine(base, "a:b.EXMOD") discards `base`
        // entirely on Windows because "a:b.EXMOD" looks drive-qualified.
        Assert.False(AssetPathGuard.IsSimpleFileName("a:b"));

        var combined = Path.Combine(@"C:\SomeBase", "a:b.EXMOD");
        Assert.NotEqual(@"C:\SomeBase\a:b.EXMOD", combined); // demonstrates why the guard is needed
    }
}
