using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Researched 2026-10-23 against the game's real tables (3.0.33).</summary>
public sealed class SaveGameNamesTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string relative, string json)
    {
        var path = Path.Combine(_data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>Seven creatures (Bear_Cub, Kea, Roat…) leave TotalPointsRequired out and take the table's Defaults 1000; they showed "/ 0".</summary>
    [Fact]
    public void Bestiary_RowsTakeTheTablesDefaults()
    {
        Write(@"Bestiary\D_BestiaryData.json", """
            { "Defaults": { "TotalPointsRequired": 1000, "Image": "None" },
              "Rows": [ { "Name": "Bear_Cub" }, { "Name": "Wolf", "TotalPointsRequired": 3000 } ] }
            """);
        var names = new SaveGameNames(_data);

        Assert.Equal(1000, names.BestiaryCreatures["Bear_Cub"].PointsRequired);
        Assert.Null(names.BestiaryCreatures["Bear_Cub"].ImagePath); // "None" is no image
        Assert.Equal(3000, names.BestiaryCreatures["Wolf"].PointsRequired);
    }

    /// <summary>All 329 workshop research rows have no name of their own and showed as "Workshop_Envirosuit_6"; the name is the item they research.</summary>
    [Fact]
    public void WorkshopTalents_AreNamedAfterTheItemTheyResearch()
    {
        Write(@"Talents\D_Talents.json", """
            { "Rows": [ { "Name": "Workshop_Envirosuit_6", "ExtraData": { "RowName": "Meta_Envirosuit3", "DataTableName": "D_WorkshopItems" }, "TalentTree": { "RowName": "Workshop_Envirosuits" } } ] }
            """);
        Write(@"MetaWorkshop\D_WorkshopItems.json", """
            { "Rows": [ { "Name": "Meta_Envirosuit3", "Item": { "RowName": "Envirosuit_Tier3" } } ] }
            """);
        Write(@"Items\D_ItemTemplate.json", """
            { "Rows": [ { "Name": "Envirosuit_Tier3", "ItemStaticData": { "RowName": "Envirosuit_Static3" } } ] }
            """);
        Write(@"Items\D_ItemsStatic.json", """
            { "Rows": [ { "Name": "Envirosuit_Static3", "Itemable": { "RowName": "Item_Envirosuit3" } } ] }
            """);
        Write(@"Traits\D_Itemable.json", """
            { "Rows": [ { "Name": "Item_Envirosuit3", "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit3-DisplayName\", \"Composite Envirosuit\")" } ] }
            """);

        Assert.Equal("Composite Envirosuit", new SaveGameNames(_data).Talents["Workshop_Envirosuit_6"].DisplayName);
    }
}
