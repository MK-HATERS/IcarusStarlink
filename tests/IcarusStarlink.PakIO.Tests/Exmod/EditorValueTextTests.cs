using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

public class EditorValueTextTests
{
    [Fact]
    public void Display_ShowsGameTextNumbersAndJson()
    {
        Assert.Equal("Xigo Envirosuit", EditorValueText.Display(JsonValue.Create("NSLOCTEXT(\"D_Itemable\", \"Item-DisplayName\", \"Xigo Envirosuit\")")));
        Assert.Equal("0.5", EditorValueText.Display(JsonNode.Parse("0.5")));
        Assert.Equal("100", EditorValueText.Display(JsonValue.Create(100)));
        Assert.Equal("", EditorValueText.Display(null));
        Assert.Equal("[1,2]", EditorValueText.Display(JsonNode.Parse("[1,2]")));
    }

    [Fact]
    public void Plain_ShowsALinksRowAndHowManyStats()
    {
        Assert.Equal("Decay_General", EditorValueText.Plain(JsonNode.Parse("""{"RowName":"Decay_General","DataTableName":"D_Decayable"}""")));
        Assert.Equal("2 stat(s)", EditorValueText.Plain(JsonNode.Parse("""{"(Value=\"BaseColdResistance_%\")":5,"(Value=\"BaseOxygenSlots_+\")":1}""")));
    }

    [Theory]
    [InlineData("0.5", true)]
    [InlineData(" 50 ", true)]
    [InlineData("0,5", false)]
    [InlineData("Infinity", false)]
    [InlineData("", false)]
    public void TryParseNumber_TakesADotForDecimals_AndOnlyFiniteNumbers(string text, bool ok) =>
        Assert.Equal(ok, EditorValueText.TryParseNumber(text, out _));

    [Theory]
    [InlineData("15", true)]
    [InlineData("-50", true)]
    [InlineData("5.5", false)]
    [InlineData("3000000000", false)]
    public void TryParseWholeNumber_RefusesFractionsAndNumbersAnIntCannotHold(string text, bool ok) =>
        Assert.Equal(ok, EditorValueText.TryParseWholeNumber(text, out _));
}
