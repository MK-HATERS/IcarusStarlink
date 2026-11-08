using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodPackageTests
{
    private static ExmodPackage Mod(params string[] files) => new()
    {
        Name = "m", Author = "a", Version = "1", Description = "", FileName = "m",
        Rows = [.. files.Select(f => new ExmodFileRow { CurrentFile = f })],
    };

    /// <summary>A new table goes before the EndOfMod marker, which every real EXMOD keeps last.</summary>
    [Fact]
    public void GetOrAddRow_ANewTable_GoesBeforeEndOfMod()
    {
        var mod = Mod("Traits-D_Itemable.json", "EndOfMod");

        var added = mod.GetOrAddRow("Traits-D_Armour.json");

        Assert.Equal(["Traits-D_Itemable.json", "Traits-D_Armour.json", "EndOfMod"], mod.Rows.Select(r => r.CurrentFile));
        Assert.Same(added, mod.Rows[1]);
        Assert.Empty(added.FileItems);
    }

    [Fact]
    public void GetOrAddRow_ATableTheModHas_IsFoundIgnoringCase()
    {
        var mod = Mod("Traits-D_Itemable.json", "EndOfMod");

        Assert.Same(mod.Rows[0], mod.GetOrAddRow("traits-d_itemable.json"));
        Assert.Equal(2, mod.Rows.Count);
    }

    [Fact]
    public void GetOrAddRow_WithoutEndOfMod_AddsAtTheEnd()
    {
        var mod = Mod("Traits-D_Itemable.json");

        mod.GetOrAddRow("Traits-D_Armour.json");

        Assert.Equal(["Traits-D_Itemable.json", "Traits-D_Armour.json"], mod.Rows.Select(r => r.CurrentFile));
    }
}
