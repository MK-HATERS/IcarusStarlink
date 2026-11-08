using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>
/// What UE4SS itself starts from mods.txt (RE-UE4SS start_mods): lines with ';' or of 4
/// characters or fewer are skipped, every space is removed, the name before the first ':' must
/// equal the folder's stem exactly (case counts), and any matching line whose value starts with
/// '1' starts the mod. Starlink's own lookup keeps inner spaces, ignores case and takes the
/// first line, so a folder like "My Mod" read as on while UE4SS never started it.
/// </summary>
public sealed class Ue4ssModsTxtTests
{
    [Theory]
    [InlineData("My Mod : 1", "My Mod", null)]
    [InlineData("x : 1", "X", null)]
    [InlineData("X : 0\r\nX : 1", "X", true)]
    [InlineData("X : 0", "X", false)]
    [InlineData("; X : 1", "X", null)]
    [InlineData("Foo : 1", "Foo.Bar", true)]
    [InlineData("M:1", "M", null)]
    [InlineData("M : 1", "M", true)]
    [InlineData("MyMod : 1", "My Mod", null)]
    [InlineData("", "X", null)]
    public void StartsByList_FollowsUe4ssOwnRules(string text, string folderName, bool? expected)
    {
        Assert.Equal(expected, Ue4ssModsTxt.Parse(text).StartsByList(folderName));
    }
}
