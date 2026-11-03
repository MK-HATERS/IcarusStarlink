using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Editor's bulk edit arithmetic and words.</summary>
public sealed class BulkEditTests
{
    [Theory]
    [InlineData(BulkOperation.Multiply, 2, 50, true, 100)]
    [InlineData(BulkOperation.Multiply, 0.5, 5, true, 3)] // rounded, half away from zero
    [InlineData(BulkOperation.Multiply, 0.5, 0.5, false, 0.25)]
    [InlineData(BulkOperation.Multiply, 0.5, 1, true, 1)] // half of 1 ingredient is still 1
    [InlineData(BulkOperation.Add, -10, 4, true, 1)] // an add doesn't take 1 or more below 1 either
    [InlineData(BulkOperation.Add, 5, 0, true, 5)]
    [InlineData(BulkOperation.Set, 0, 80, true, 0)] // zero only on purpose, with Set
    public void Apply_RoundsWholeNumbers_AndKeepsAmountsAboveZero(BulkOperation operation, double amount, double current, bool whole, double expected)
    {
        Assert.Equal(expected, BulkEdit.Apply(operation, amount, current, whole));
    }

    /// <summary>A preview row typed over: one number per value (three ingredients take "2, 1, 5"), whole numbers where the game has only those.</summary>
    [Theory]
    [InlineData("2, 1, 5", true, null)]
    [InlineData("2,1", false, "Needs 3 numbers, one per value, with commas between.")]
    [InlineData("2, x, 5", false, "'x' isn't a number.")]
    [InlineData("2, 1.5, 5", false, "Whole numbers only here.")]
    public void TypedValues_OnePerValue_WholeWhereTheGameIs(string text, bool ok, string? error)
    {
        Assert.Equal(ok, BulkEdit.TryReadValues(text, [true, true, true], out var values, out var why));
        Assert.Equal(error, why);
        if (ok)
        {
            Assert.Equal([2, 1, 5], values);
        }
    }

    [Fact]
    public void TypedValue_TakesAFraction_WhereTheGameDoes()
    {
        Assert.True(BulkEdit.TryReadValues(" 1.5 ", [false], out var values, out _));
        Assert.Equal([1.5], values);
        Assert.False(BulkEdit.TryReadValues("1, 2", [false], out _, out var error));
        Assert.Equal("Needs one number.", error);
    }

    [Fact]
    public void Apply_RefusesWhatIsntANumber()
    {
        Assert.Null(BulkEdit.Apply(BulkOperation.Multiply, double.MaxValue, double.MaxValue, false));
    }

    /// <summary>A recipe card is "Recipe:Bread" on one item and "Recipe:Stone_Furnace" on another, and the ingredients differ: "every ingredient amount" is the one field they share.</summary>
    [Fact]
    public void Key_IsTheCardKindAndPath_WithListPositionsAsEvery()
    {
        var key = BulkFieldKey.Of("Recipe:Bread", new EditPath("Inputs", [2, "Count"]));

        Assert.Equal(new BulkFieldKey("Recipe", "Inputs/*/Count"), key);
        Assert.True(key.IsEveryEntry);
        Assert.Equal("Recipe · every ingredient amount", BulkEdit.LabelOf(key, "Recipe 1 of 2 · Crafting bench", "Needs Stone"));
        Assert.Equal(new BulkFieldKey("Armour", "ArmourStats/(Value=\"BaseOxygenSlots_+\")"), BulkFieldKey.Of("Armour", new EditPath("ArmourStats", ["(Value=\"BaseOxygenSlots_+\")"])));
        Assert.Equal("Name, icon, weight · Weight", BulkEdit.LabelOf(new BulkFieldKey("Itemable", "Weight"), "Name, icon, weight", "Weight"));
    }
}
