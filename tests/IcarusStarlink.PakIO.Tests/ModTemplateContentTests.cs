using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ModTemplateContentTests
{
    [Fact]
    public void Create_Blank_ReturnsEmptyRowsWithGivenNameAndAuthor()
    {
        var package = ModTemplateContent.Create(ModTemplate.Blank, "My Mod", "Author");

        Assert.Equal("My Mod", package.Name);
        Assert.Equal("Author", package.Author);
        Assert.Equal("My_Mod", package.FileName);
        Assert.Empty(package.Rows);
    }

    [Theory]
    [InlineData(ModTemplate.CraftableOrDeployableItem)]
    [InlineData(ModTemplate.ConsumableItem)]
    [InlineData(ModTemplate.BuildingPiece)]
    [InlineData(ModTemplate.ElectricGenerator)]
    [InlineData(ModTemplate.WaterPump)]
    public void Create_RealTemplate_SetsRealNameAuthorFileNameAndProducesRows(ModTemplate template)
    {
        var package = ModTemplateContent.Create(template, "My New Item", "Some Author");

        Assert.Equal("My New Item", package.Name);
        Assert.Equal("Some Author", package.Author);
        Assert.Equal("My_New_Item", package.FileName);
        Assert.NotEmpty(package.Rows);
    }

    [Fact]
    public void Create_CraftableOrDeployableItem_SubstitutesPlaceholderEverywhereIncludingCrossReferences()
    {
        var package = ModTemplateContent.Create(ModTemplate.CraftableOrDeployableItem, "Fancy Torch", "Author");

        var staticRow = Assert.Single(package.Rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
        var item = Assert.Single(staticRow.FileItems);
        Assert.Equal("Fancy_Torch", item.Name);

        // A nested cross-reference (Meshable -> "Mesh_TempName") must be substituted too, not just
        // the top-level item Name — the whole point of a single global text replace before parsing.
        var meshable = Assert.IsType<System.Text.Json.Nodes.JsonObject>(item.Fields["Meshable"]);
        Assert.Equal("Mesh_Fancy_Torch", meshable["RowName"]!.GetValue<string>());

        var itemable = Assert.IsType<System.Text.Json.Nodes.JsonObject>(item.Fields["Itemable"]);
        Assert.Equal("Item_Fancy_Torch", itemable["RowName"]!.GetValue<string>());

        // Every real item this session's own research confirmed always needs Generated_Tags —
        // the template must carry it through the substitution intact, not lose it.
        Assert.True(item.Fields.ContainsKey("Generated_Tags"));
    }

    [Fact]
    public void Create_ConsumableItem_SubstitutesPlaceholderAndKeepsGeneratedTags()
    {
        var package = ModTemplateContent.Create(ModTemplate.ConsumableItem, "Health Drink", "Author");

        var staticRow = Assert.Single(package.Rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
        var item = Assert.Single(staticRow.FileItems);
        Assert.Equal("Health_Drink", item.Name);
        Assert.True(item.Fields.ContainsKey("Generated_Tags"));

        var consumable = Assert.IsType<System.Text.Json.Nodes.JsonObject>(item.Fields["Consumable"]);
        Assert.Equal("Health_Drink", consumable["RowName"]!.GetValue<string>());
    }

    [Fact]
    public void Create_BuildingPiece_SubstitutesPlaceholderEverywhereIncludingCrossReferences()
    {
        var package = ModTemplateContent.Create(ModTemplate.BuildingPiece, "Reed Wall", "Author");

        var staticRow = Assert.Single(package.Rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
        var item = Assert.Single(staticRow.FileItems);
        Assert.Equal("Reed_Wall", item.Name);

        var itemable = Assert.IsType<System.Text.Json.Nodes.JsonObject>(item.Fields["Itemable"]);
        Assert.Equal("Item_Reed_Wall", itemable["RowName"]!.GetValue<string>());

        var buildable = Assert.IsType<System.Text.Json.Nodes.JsonObject>(item.Fields["Buildable"]);
        Assert.Equal("Reed_Wall", buildable["RowName"]!.GetValue<string>());

        Assert.True(item.Fields.ContainsKey("Generated_Tags"));

        // Real classic-IMM behavior, distinct from Craftable/Consumable's real vanilla assets: this
        // template has no existing vanilla Blueprint to point at, so the placeholder deliberately
        // carries through into the asset path itself, for the author's own real art to replace.
        var buildingRow = Assert.Single(package.Rows, r => r.CurrentFile == "Building-D_BuildingPieces.json");
        var buildingItem = Assert.Single(buildingRow.FileItems);
        Assert.Equal("/Game/BP/Building/Wood/BP_Reed_Wall.BP_Reed_Wall_C", buildingItem.Fields["Blueprint"]!.GetValue<string>());
    }

    [Fact]
    public void Create_ElectricGenerator_SubstitutesPlaceholderAndKeepsGeneratedTags()
    {
        var package = ModTemplateContent.Create(ModTemplate.ElectricGenerator, "Wind Turbine", "Author");

        var staticRow = Assert.Single(package.Rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
        var item = Assert.Single(staticRow.FileItems);
        Assert.Equal("Wind_Turbine", item.Name);
        Assert.True(item.Fields.ContainsKey("Generated_Tags"));

        var meshable = Assert.IsType<System.Text.Json.Nodes.JsonObject>(item.Fields["Meshable"]);
        Assert.Equal("Mesh_Wind_Turbine", meshable["RowName"]!.GetValue<string>());

        var energyRow = Assert.Single(package.Rows, r => r.CurrentFile == "Traits-D_Energy.json");
        var energyItem = Assert.Single(energyRow.FileItems);
        Assert.Equal("Wind_Turbine", energyItem.Name);
        Assert.Equal("Produce", energyItem.Fields["FlowType"]!.GetValue<string>());
    }

    [Fact]
    public void Create_WaterPump_SubstitutesPlaceholderAndKeepsGeneratedTags()
    {
        var package = ModTemplateContent.Create(ModTemplate.WaterPump, "Reservoir Pump", "Author");

        var staticRow = Assert.Single(package.Rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
        var item = Assert.Single(staticRow.FileItems);
        Assert.Equal("Reservoir_Pump", item.Name);
        Assert.True(item.Fields.ContainsKey("Generated_Tags"));

        var generatorRow = Assert.Single(package.Rows, r => r.CurrentFile == "Traits-D_Generator.json");
        var generatorItem = Assert.Single(generatorRow.FileItems);
        Assert.Equal("Reservoir_Pump", generatorItem.Name);
        var resource = Assert.IsType<System.Text.Json.Nodes.JsonObject>(generatorItem.Fields["Resource"]);
        Assert.Equal("Water", resource["Value"]!.GetValue<string>());
    }

    [Fact]
    public void Create_NameWithSpacesAndPunctuation_SanitizesToPlainIdentifierForRowNames()
    {
        var package = ModTemplateContent.Create(ModTemplate.CraftableOrDeployableItem, "Bob's Cool-Item!", "Author");

        var staticRow = Assert.Single(package.Rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
        var item = Assert.Single(staticRow.FileItems);

        // The display Name/FileName can keep the original text — only the substituted RowName-style
        // identifier needs to be a plain identifier.
        Assert.Equal("Bob's Cool-Item!", package.Name);
        Assert.DoesNotContain("'", item.Name);
        Assert.DoesNotContain("!", item.Name);
        Assert.DoesNotContain("-", item.Name);
    }

    [Fact]
    public void Create_RealTemplate_EndsWithEndOfModSentinel()
    {
        var package = ModTemplateContent.Create(ModTemplate.CraftableOrDeployableItem, "Torch", "Author");

        Assert.Equal("EndOfMod", package.Rows[^1].CurrentFile);
    }
}
