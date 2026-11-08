using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.Tests.Views;

public sealed class ThemedConfirmDialogTests
{
    [Fact]
    public void CopyText_LaysOutTitleMessageAndButtonsLikeTheWindowsMessageBox()
    {
        var text = ThemedConfirmDialog.CopyText("Delete mod", "Delete Better Bows?\nThis can't be undone.", ["No", "Yes"]);

        Assert.Equal(
            "---------------------------\r\n"
            + "Delete mod\r\n"
            + "---------------------------\r\n"
            + "Delete Better Bows?\r\nThis can't be undone.\r\n"
            + "---------------------------\r\n"
            + "No   Yes   \r\n"
            + "---------------------------\r\n",
            text);
    }

    [Theory]
    [InlineData(1.0, 624)]
    [InlineData(1.5, 416)]
    public void MessageMaxHeight_AtAnyTextSize_TakesAtMost60PercentOfTheScreen(double scale, double expected)
    {
        // Regression: at the 150% text size the message area grew to 0.9 of the screen, pushing Yes and No off it.
        Assert.Equal(expected, ThemedConfirmDialog.MessageMaxHeight(1040, scale), precision: 6);
    }

    [Fact]
    public void MessageMaxHeight_OnATinyScreen_KeepsRoomForAFewLines()
    {
        Assert.Equal(120, ThemedConfirmDialog.MessageMaxHeight(100, 1.5));
    }
}
