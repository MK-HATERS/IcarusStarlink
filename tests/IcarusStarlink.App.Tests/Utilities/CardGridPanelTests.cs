using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

public class CardGridPanelTests
{
    [Theory]
    [InlineData(1000, 330, 12, 2)]
    [InlineData(1020, 330, 12, 3)]
    [InlineData(200, 330, 12, 1)]
    [InlineData(2000, 330, 12, 5)]
    public void ColumnsFor_FitsAsManyMinWidthColumnsAsTheWidthAllows(double width, double min, double spacing, int expected)
    {
        Assert.Equal(expected, CardGridPanel.ColumnsFor(width, min, spacing));
    }

    [Fact]
    public void ColumnsFor_UnboundedWidth_IsOneColumn()
    {
        Assert.Equal(1, CardGridPanel.ColumnsFor(double.PositiveInfinity, 330, 12));
    }
}
