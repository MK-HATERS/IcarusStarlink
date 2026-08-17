using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

public class DefaultSemanticClassifierTests
{
    private readonly DefaultSemanticClassifier _classifier = new();

    [Fact]
    public void Classify_RowNameShape_IsRowReference()
    {
        var value = JsonNode.Parse("""{"RowName": "SwordRecipe", "DataTableName": "D_ProcessorRecipes"}""");

        var semantic = _classifier.Classify("Items-D_ItemsStatic.json", "CraftingRecipe", value);

        Assert.Equal(ValueSemantic.RowReference, semantic);
    }

    [Theory]
    [InlineData("UnlockTagQuery")]
    [InlineData("RequiredTagRequirements")]
    public void Classify_TagQueryFieldNames_AreGameplayTagQuery(string fieldName)
    {
        var semantic = _classifier.Classify("Deployables-D_DeployableSetup.json", fieldName, JsonNode.Parse("[]"));

        Assert.Equal(ValueSemantic.GameplayTagQuery, semantic);
    }

    [Fact]
    public void Classify_ScalarValue_IsScalar()
    {
        var semantic = _classifier.Classify("Items-D_ItemsStatic.json", "Damage", JsonValue.Create(10));

        Assert.Equal(ValueSemantic.Scalar, semantic);
    }

    [Fact]
    public void Classify_CompoundValueWithoutRowNameShape_IsGenericCompound()
    {
        var value = JsonNode.Parse("""{"Min": 1, "Max": 5}""");

        var semantic = _classifier.Classify("Items-D_ItemsStatic.json", "StackSizeRange", value);

        Assert.Equal(ValueSemantic.GenericCompound, semantic);
    }

    [Fact]
    public void Classify_CustomRules_OverrideDefaults()
    {
        var classifier = new DefaultSemanticClassifier([("Special*", ValueSemantic.GameplayTagQuery)]);

        var semantic = classifier.Classify("Items-D_ItemsStatic.json", "SpecialField", JsonValue.Create(1));

        Assert.Equal(ValueSemantic.GameplayTagQuery, semantic);
    }
}
