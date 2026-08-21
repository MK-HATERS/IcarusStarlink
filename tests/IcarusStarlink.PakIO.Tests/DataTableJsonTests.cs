using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Tests;

public class DataTableJsonTests
{
    [Fact]
    public void RowsToKeyedObject_ConvertsArrayOfNamedRowsToNameKeyedObject()
    {
        // Shape confirmed against a real extracted Content\Data\data.pak file.
        var file = JsonNode.Parse("""
            {
                "RowStruct": "/Script/Icarus.Fuel",
                "Defaults": {},
                "Rows": [
                    {"Name": "Composter", "FlowType": "Produce", "ResourceFlowRate": 10},
                    {"Name": "Generator", "ResourceFlowRate": 10}
                ]
            }
            """)!.AsObject();

        var keyed = DataTableJson.RowsToKeyedObject(file);

        Assert.Equal(2, keyed.Count);
        Assert.Equal("Produce", keyed["Composter"]!["FlowType"]!.GetValue<string>());
        Assert.Equal(10, keyed["Generator"]!["ResourceFlowRate"]!.GetValue<int>());
    }

    [Fact]
    public void RowsToKeyedObject_NameFieldIsRemovedFromTheRowItself()
    {
        var file = JsonNode.Parse("""{"Rows": [{"Name": "Composter", "ResourceFlowRate": 10}]}""")!.AsObject();

        var keyed = DataTableJson.RowsToKeyedObject(file);

        Assert.False(keyed["Composter"]!.AsObject().ContainsKey("Name"));
    }

    [Fact]
    public void RowsToKeyedObject_MissingRowsKey_ReturnsEmptyObject()
    {
        var file = JsonNode.Parse("""{"RowStruct": "/Script/Icarus.Fuel"}""")!.AsObject();

        Assert.Empty(DataTableJson.RowsToKeyedObject(file));
    }

    [Fact]
    public void RowsToKeyedObject_RowWithNoNameField_IsSkippedNotThrown()
    {
        var file = JsonNode.Parse("""{"Rows": [{"ResourceFlowRate": 10}, {"Name": "Generator", "ResourceFlowRate": 5}]}""")!.AsObject();

        var keyed = DataTableJson.RowsToKeyedObject(file);

        Assert.Single(keyed);
        Assert.True(keyed.ContainsKey("Generator"));
    }
}
