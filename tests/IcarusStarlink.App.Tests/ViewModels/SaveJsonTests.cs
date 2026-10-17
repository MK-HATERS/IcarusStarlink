using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Save values in a changed shape read as their meaning, or null, never an exception.</summary>
public class SaveJsonTests
{
    [Theory]
    [InlineData("3", 3)]
    [InlineData("3.0", 3)]
    [InlineData("\"3\"", 3)]
    [InlineData("\" 12 \"", 12)]
    [InlineData("-4", -4)]
    public void Int_AcceptsWholeNumbersInAnyShape(string json, int expected) =>
        Assert.Equal(expected, SaveJson.Int(JsonNode.Parse(json)));

    [Theory]
    [InlineData("3.5")]
    [InlineData("\"abc\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("4294967296")]
    public void Int_NotAWholeIntNumber_IsNull(string json) =>
        Assert.Null(SaveJson.Int(JsonNode.Parse(json)));

    [Fact]
    public void Int_ValueBuiltInCode_Reads()
    {
        Assert.Equal(7, SaveJson.Int(JsonValue.Create(7)));
        Assert.Equal(7, SaveJson.Int(JsonValue.Create(7L)));
        Assert.Equal(7, SaveJson.Int(JsonValue.Create(7.0)));
        Assert.Equal(7L, SaveJson.Long(JsonValue.Create((short)7)));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"False\"", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"1\"", true)]
    public void Bool_AcceptsTheUsualShapes(string json, bool expected) =>
        Assert.Equal(expected, SaveJson.Bool(JsonNode.Parse(json)));

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("{}")]
    [InlineData("1.5")]
    public void Bool_Unreadable_IsNull(string json) =>
        Assert.Null(SaveJson.Bool(JsonNode.Parse(json)));

    [Fact]
    public void String_ReadsStringsAndNumbers_NotObjects()
    {
        Assert.Equal("Wolf", SaveJson.String(JsonNode.Parse("\"Wolf\"")));
        Assert.Equal("12", SaveJson.String(JsonNode.Parse("12")));
        Assert.Null(SaveJson.String(JsonNode.Parse("{\"RowName\":\"Wolf\"}")));
        Assert.Null(SaveJson.String(null));
    }

    [Fact]
    public void IdArray_SkipsWhatItCantRead()
    {
        Assert.Equal([1, 2, 3], SaveJson.IdArray(JsonNode.Parse("[1, \"2\", 3.0, -1, \"x\", {}, null, 2.5]")));
        Assert.Empty(SaveJson.IdArray(JsonNode.Parse("{}")));
    }
}
