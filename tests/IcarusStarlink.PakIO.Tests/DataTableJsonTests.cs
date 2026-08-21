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

    [Fact]
    public void KeyedObjectToRows_RoundTripsThroughRowsToKeyedObject()
    {
        var original = JsonNode.Parse("""
            {
                "RowStruct": "/Script/Icarus.Fuel",
                "Defaults": {"SomeDefault": true},
                "Rows": [
                    {"Name": "Composter", "FlowType": "Produce", "ResourceFlowRate": 10},
                    {"Name": "Generator", "ResourceFlowRate": 10}
                ]
            }
            """)!.AsObject();

        var keyed = DataTableJson.RowsToKeyedObject(original);
        var rebuilt = DataTableJson.KeyedObjectToRows(original, keyed);

        Assert.True(JsonNode.DeepEquals(original, rebuilt));
    }

    [Fact]
    public void KeyedObjectToRows_PreservesRowStructAndDefaultsFromOriginalFile()
    {
        var original = JsonNode.Parse("""{"RowStruct": "/Script/Icarus.Fuel", "Defaults": {"X": 1}, "Rows": []}""")!.AsObject();
        var keyed = new JsonObject();

        var rebuilt = DataTableJson.KeyedObjectToRows(original, keyed);

        Assert.Equal("/Script/Icarus.Fuel", rebuilt["RowStruct"]!.GetValue<string>());
        Assert.Equal(1, rebuilt["Defaults"]!["X"]!.GetValue<int>());
    }

    [Fact]
    public void KeyedObjectToRows_NameIsReinsertedAsTheFirstFieldOfEachRow()
    {
        var original = new JsonObject { ["Rows"] = new JsonArray() };
        var keyed = new JsonObject { ["Composter"] = new JsonObject { ["ResourceFlowRate"] = 10 } };

        var rebuilt = DataTableJson.KeyedObjectToRows(original, keyed);

        var row = rebuilt["Rows"]!.AsArray()[0]!.AsObject();
        Assert.Equal("Name", row.First().Key);
        Assert.Equal("Composter", row["Name"]!.GetValue<string>());
        Assert.Equal(10, row["ResourceFlowRate"]!.GetValue<int>());
    }

    [Fact]
    public void KeyedObjectToRows_ReflectsChangesMadeToTheKeyedTable()
    {
        var original = JsonNode.Parse("""{"Rows": [{"Name": "Composter", "ResourceFlowRate": 10}]}""")!.AsObject();
        var keyed = DataTableJson.RowsToKeyedObject(original);
        keyed["Composter"]!["ResourceFlowRate"] = 999;

        var rebuilt = DataTableJson.KeyedObjectToRows(original, keyed);

        var row = rebuilt["Rows"]!.AsArray()[0]!.AsObject();
        Assert.Equal(999, row["ResourceFlowRate"]!.GetValue<int>());
    }
}
