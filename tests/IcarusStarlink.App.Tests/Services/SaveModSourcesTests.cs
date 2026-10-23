using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.Services;

public sealed class SaveModSourcesTests
{
    private const string Talents = SaveModSources.TalentsFile;

    private static SaveModSources.Index Index(bool canTellInstalled = true) => new(
        new Dictionary<string, HashSet<string>> { [Talents] = new(StringComparer.OrdinalIgnoreCase) { "Wood_Sticks" } },
        new Dictionary<string, HashSet<string>> { [Talents] = new(StringComparer.OrdinalIgnoreCase) { "Workshop_Moa" } },
        new Dictionary<string, Dictionary<string, List<string>>>
        {
            [Talents] = new(StringComparer.OrdinalIgnoreCase) { ["Workshop_Moa"] = ["Ferani Buyable Beasts"], ["Workshop_Agents_Bow"] = ["Agents"] },
        },
        // The shipped history: T4_Weapon_Rack was a game talent, since removed (found on the user's save, 2026-10-23).
        GameRowHistory.Load(null),
        canTellInstalled);

    [Fact]
    public void RowsAreToldApartByWhereTheyComeFrom()
    {
        var index = Index();

        Assert.Null(index.Classify(Talents, "Wood_Sticks"));
        Assert.Equal(SaveRowOrigin.InstalledMod, index.Classify(Talents, "Workshop_Moa")!.Origin);
        Assert.Contains("Ferani Buyable Beasts", index.Classify(Talents, "Workshop_Moa")!.Label);
        Assert.Equal(SaveRowOrigin.LibraryModNotInstalled, index.Classify(Talents, "Workshop_Agents_Bow")!.Origin);
        Assert.Equal(SaveRowOrigin.RemovedByGame, index.Classify(Talents, "T4_Weapon_Rack")!.Origin);
        Assert.Equal(SaveRowOrigin.Leftover, index.Classify(Talents, "Workshop_Petes_AuxSlot")!.Origin);

        Assert.False(index.Classify(Talents, "Workshop_Moa")!.IsRemovable);
        Assert.False(index.Classify(Talents, "Workshop_Agents_Bow")!.IsRemovable);
        Assert.True(index.Classify(Talents, "Workshop_Petes_AuxSlot")!.IsRemovable);
    }

    /// <summary>The user's bank held LegendaryWeapon_ApeClub, the template's name; the item row is LegendaryWeapon_Ape_Club (2026-10-23).</summary>
    [Fact]
    public void ItemSavedUnderItsTemplateName_IsMisnamed_NotALeftover()
    {
        var index = new SaveModSources.Index(
            new Dictionary<string, HashSet<string>> { [SaveModSources.ItemsFile] = new(StringComparer.OrdinalIgnoreCase) { "LegendaryWeapon_Ape_Club" } },
            new Dictionary<string, HashSet<string>>(),
            new Dictionary<string, Dictionary<string, List<string>>>(),
            GameRowHistory.Empty,
            canTellInstalled: true,
            new Dictionary<string, string> { ["LegendaryWeapon_ApeClub"] = "LegendaryWeapon_Ape_Club" });

        var source = index.Classify(SaveModSources.ItemsFile, "LegendaryWeapon_ApeClub")!;

        Assert.Equal(SaveRowOrigin.Misnamed, source.Origin);
        Assert.False(source.IsRemovable);
        Assert.Contains("LegendaryWeapon_Ape_Club", source.Label);
    }

    /// <summary>WirelessPower adds its talent through an ArcFission rows.lua (found on the user's setup, 2026-10-23).</summary>
    [Fact]
    public void ArcFissionRowsLua_IsReadPerTableAndMod()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_Ue4ss", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "WirelessPower", "ArcFission"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "WirelessPower", "ArcFission", "rows.lua"), """
                -- ArcFission rows (format 1)
                return {
                    format = 1,
                    tables = {
                        ["Items/D_ItemsStatic.json"] = {
                            { name = "Item_Wireless_Power_Node", json = [[        {
                            "Name": "Item_Wireless_Power_Node"
                        }]] },
                        },
                        ["Talents/D_Talents.json"] = {
                            { name = "Wireless_Power_Node", json = [[        {
                            "Name": "Wireless_Power_Node",
                            "RowName": "Something_Else"
                        }]] },
                        },
                    },
                }
                """);

            Assert.Equal(
                [("Items-D_ItemsStatic.json", "Item_Wireless_Power_Node", "WirelessPower"), (Talents, "Wireless_Power_Node", "WirelessPower")],
                SaveModSources.ArcFissionRowsUnder(folder).ToList());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Without knowing what's installed, nothing may be called a leftover.</summary>
    [Fact]
    public void WhenTheInstalledModsCantBeRead_NothingIsALeftover()
    {
        var index = Index(canTellInstalled: false);

        Assert.Null(index.Classify(Talents, "Workshop_Petes_AuxSlot"));
        Assert.Null(index.Classify(Talents, "T4_Weapon_Rack"));
        Assert.Equal(SaveRowOrigin.LibraryModNotInstalled, index.Classify(Talents, "Workshop_Agents_Bow")!.Origin);
    }
}
