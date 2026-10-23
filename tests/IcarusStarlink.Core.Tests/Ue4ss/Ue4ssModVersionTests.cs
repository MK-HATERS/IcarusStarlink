using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class Ue4ssModVersionTests
{
    [Theory]
    [InlineData("local VERSION = \"1.9.0\"", "1.9.0")]
    [InlineData("  local VERSION = '0.10.1'  -- shown in the log", "0.10.1")]
    [InlineData("VERSION = \"v2.2\"", "2.2")]
    [InlineData("local MOD_VERSION = \"3.0.0-beta\"", "3.0.0-beta")]
    public void FromLines_ReadsTheDeclaredVersion(string line, string expected) =>
        Assert.Equal(expected, Ue4ssModVersion.FromLines(["-- MkHaters Bags", line, "print(VERSION)"]));

    [Theory]
    [InlineData("print(\"version 1.0\")")]
    [InlineData("local versionCheck = true")]
    [InlineData("-- VERSION = \"1.0\" is set below")]
    public void FromLines_NoDeclaration_IsNull(string line) =>
        Assert.Null(Ue4ssModVersion.FromLines([line]));

    [Fact]
    public void TryRead_NoMainLua_IsNull() =>
        Assert.Null(Ue4ssModVersion.TryRead(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
}
